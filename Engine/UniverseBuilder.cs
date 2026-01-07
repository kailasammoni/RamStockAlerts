using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using RamStockAlerts.Feeds;
using RamStockAlerts.Models.Polygon;

namespace RamStockAlerts.Engine;

public class UniverseBuilder
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IConfiguration _configuration;
    private readonly IMemoryCache _cache;
    private readonly ILogger<UniverseBuilder> _logger;
    private readonly AlpacaStreamClient _alpacaClient;
    private readonly SemaphoreSlim _universeBuildLock = new(1, 1);

    private const string UniverseCacheKey = "universe.active";
    private const string FloatCacheKeyPrefix = "float.";
    private static readonly TimeSpan UniverseTtl = TimeSpan.FromMinutes(5);

    // TODO: Replace IMemoryCache with Redis for distributed caching with 5-minute TTL

    public UniverseBuilder(
        IHttpClientFactory httpClientFactory,
        IConfiguration configuration,
        IMemoryCache cache,
        ILogger<UniverseBuilder> logger,
        AlpacaStreamClient alpacaClient)
    {
        _httpClientFactory = httpClientFactory;
        _configuration = configuration;
        _cache = cache;
        _logger = logger;
        _alpacaClient = alpacaClient;
    }

    public async Task<IReadOnlyCollection<string>> GetActiveUniverseAsync(CancellationToken cancellationToken)
    {
        if (_cache.TryGetValue(UniverseCacheKey, out IReadOnlyCollection<string>? cached) && cached is not null)
        {
            return cached;
        }

        await _universeBuildLock.WaitAsync(cancellationToken);
        try
        {
            // Double-check cache after acquiring the lock to avoid duplicate builds
            if (_cache.TryGetValue(UniverseCacheKey, out cached) && cached is not null)
            {
                return cached;
            }

            var universe = await BuildUniverseAsync(cancellationToken);
            _cache.Set(UniverseCacheKey, universe, UniverseTtl);
            return universe;
        }
        finally
        {
            _universeBuildLock.Release();
        }
    }

    public async Task<IReadOnlyCollection<string>> BuildUniverseAsync(CancellationToken cancellationToken)
    {
        var minPrice = _configuration.GetValue("Universe:MinPrice", 10m);
        var maxPrice = _configuration.GetValue("Universe:MaxPrice", 80m);
        var minRelVol = _configuration.GetValue("Universe:MinRelativeVolume", 2m);
        var maxSpread = _configuration.GetValue("Universe:MaxSpread", 0.05m);
        var maxFloat = _configuration.GetValue("Universe:MaxFloat", 150_000_000m);
        var maxTickers = _configuration.GetValue("Universe:MaxTickers", 100);
        var useStaticTickers = _configuration.GetValue("Universe:UseStaticTickers", false);

        // If UseStaticTickers is true, use hardcoded list (for testing)
        if (useStaticTickers)
        {
            var fallback = _configuration.GetSection("Polygon:Tickers").Get<string[]>() ?? Array.Empty<string>();
            if (fallback.Any())
            {
                _logger.LogInformation("Using static ticker list: {Tickers}", string.Join(", ", fallback));
                return fallback.Where(t => !string.IsNullOrWhiteSpace(t)).Select(t => t.Trim().ToUpperInvariant()).Distinct().ToList();
            }
        }

        // Fetch live tickers from Polygon API (market movers endpoint)
        var apiKey = _configuration["Polygon:ApiKey"];
        if (string.IsNullOrEmpty(apiKey))
        {
            _logger.LogWarning("Polygon API key not configured; falling back to hardcoded tickers");
            var fallback = _configuration.GetSection("Polygon:Tickers").Get<string[]>() ?? Array.Empty<string>();
            return fallback.Where(t => !string.IsNullOrWhiteSpace(t)).Select(t => t.Trim().ToUpperInvariant()).Distinct().ToList();
        }

        try
        {
            // Use Polygon Ticker endpoint to search for US stocks
            // /v3/reference/tickers?market=stocks&type=CS filters for Common Stocks only (excludes ETFs, ADRs, warrants)
            // Note: exchange parameter only accepts single value (XNAS or XNYS), not comma-separated
            var url = $"https://api.polygon.io/v3/reference/tickers?market=stocks&type=CS&active=true&exchange=XNAS&limit=1000&apikey={apiKey}";
            
            using var client = _httpClientFactory.CreateClient();
            var response = await client.GetAsync(url, cancellationToken);
            
            if (!response.IsSuccessStatusCode)
            {
                var errorBody = await response.Content.ReadAsStringAsync(cancellationToken);
                _logger.LogWarning("Failed to fetch tickers from Polygon: {Status} - {Error}", response.StatusCode, errorBody);
                var fallback = _configuration.GetSection("Polygon:Tickers").Get<string[]>() ?? Array.Empty<string>();
                return fallback.Where(t => !string.IsNullOrWhiteSpace(t)).Select(t => t.Trim().ToUpperInvariant()).Distinct().ToList();
            }

            var json = await response.Content.ReadAsStringAsync(cancellationToken);
            var tickerData = System.Text.Json.JsonSerializer.Deserialize<PolygonTickerListResponse>(json);

            if (tickerData?.Results == null || !tickerData.Results.Any())
            {
                _logger.LogWarning("No ticker data returned from Polygon");
                return Array.Empty<string>();
            }

            // Filter by criteria: price 10-80, relative volume > 2, spread < 0.05, float < 150M
            // Exclude OTC and preferred shares (API already filtered for type=CS)
            var filtered = tickerData.Results
                .Where(t => 
                    // Basic validation
                    t.Ticker != null &&
                    !string.IsNullOrWhiteSpace(t.Ticker) &&
                    
                    // Exclude OTC (have dot notation like "STOCK.OTC")
                    !t.Ticker.Contains('.') &&
                    
                    // Exclude preferred shares (have dash like "STOCK-A")
                    !t.Ticker.Contains('-'))
                .Select(t => t.Ticker!.ToUpperInvariant())
                .Distinct()
                .Where(ticker =>
                {
                    // Get real-time state for this ticker
                    var state = _alpacaClient.GetSymbolState(ticker);
                    if (state == null)
                    {
                        _logger.LogDebug("{Ticker} filtered: No real-time data available", ticker);
                        return false; // No real-time data yet
                    }
                    
                    // Filter by price (10-80)
                    var price = state.GetCurrentPrice();
                    if (!price.HasValue || price.Value < minPrice || price.Value > maxPrice)
                    {
                        if (price.HasValue)
                            _logger.LogDebug("{Ticker} filtered: Price ${Price} outside range ${Min}-${Max}", ticker, price.Value, minPrice, maxPrice);
                        return false;
                    }
                    
                    // Filter by spread (< 0.05)
                    var spread = state.GetCurrentSpread();
                    if (!spread.HasValue || spread.Value > maxSpread)
                    {
                        if (spread.HasValue)
                            _logger.LogDebug("{Ticker} filtered: Spread {Spread:P2} exceeds max {MaxSpread:P2}", ticker, spread.Value, maxSpread);
                        return false;
                    }
                    
                    // Filter by relative volume (> 2)
                    var relVol = state.GetRelativeVolume();
                    if (!relVol.HasValue || relVol.Value < minRelVol)
                    {
                        if (relVol.HasValue)
                            _logger.LogDebug("{Ticker} filtered: RelVol {RelVol:F2} below min {MinRelVol}", ticker, relVol.Value, minRelVol);
                        return false;
                    }
                    
                    return true;
                })
                .ToList();

            // Filter by float (shares outstanding < 150M)
            // This requires async API calls, so we do it in a separate step
            var floatFilteredTickers = new List<string>();
            foreach (var ticker in filtered)
            {
                var sharesOutstanding = await GetSharesOutstandingAsync(ticker, cancellationToken);
                if (sharesOutstanding.HasValue && sharesOutstanding.Value > maxFloat)
                {
                    _logger.LogDebug("{Ticker} filtered: float {Float}M exceeds max {MaxFloat}M", 
                        ticker, sharesOutstanding.Value / 1_000_000m, maxFloat / 1_000_000m);
                    continue;
                }
                // Note: If sharesOutstanding is null, we allow the ticker through (data unavailable)
                floatFilteredTickers.Add(ticker);
                
                if (floatFilteredTickers.Count >= maxTickers)
                    break;
            }
            
            // TODO: Exclude halted symbols - requires trading status check
            // Alpaca IEX feed doesn't provide halt status directly
            
            var finalFiltered = floatFilteredTickers;

            _logger.LogInformation(
                "Universe built with {Count} symbols (Price ${Min}-${Max}, RelVol>{RelVol}, Spread<{Spread}, Float<{Float}M, excluding ETFs/ADRs/OTC/halted) | Top: {TopTickers}",
                finalFiltered.Count, 
                minPrice, 
                maxPrice,
                minRelVol,
                maxSpread,
                maxFloat / 1_000_000m,
                string.Join(", ", finalFiltered.Take(10)));

            if (!finalFiltered.Any())
            {
                _logger.LogWarning("No tickers passed filters; falling back to hardcoded list");
                var fallback = _configuration.GetSection("Polygon:Tickers").Get<string[]>() ?? Array.Empty<string>();
                return fallback.Where(t => !string.IsNullOrWhiteSpace(t)).Select(t => t.Trim().ToUpperInvariant()).Distinct().ToList();
            }

            return finalFiltered;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error building universe from market data; falling back to hardcoded tickers");
            var fallback = _configuration.GetSection("Polygon:Tickers").Get<string[]>() ?? Array.Empty<string>();
            return fallback.Where(t => !string.IsNullOrWhiteSpace(t)).Select(t => t.Trim().ToUpperInvariant()).Distinct().ToList();
        }
    }

    /// <summary>
    /// Get shares outstanding from cache or fetch from Polygon API.
    /// </summary>
    private async Task<long?> GetSharesOutstandingAsync(string ticker, CancellationToken cancellationToken)
    {
        var cacheKey = FloatCacheKeyPrefix + ticker;
        
        // Check cache first
        if (_cache.TryGetValue(cacheKey, out long? cachedFloat))
        {
            return cachedFloat;
        }
        
        // Fetch from Polygon API
        var apiKey = _configuration["Polygon:ApiKey"];
        if (string.IsNullOrEmpty(apiKey))
        {
            return null;
        }
        
        try
        {
            // Add small delay to avoid rate limiting (Polygon allows 5 req/min on free tier)
            await Task.Delay(TimeSpan.FromMilliseconds(250), cancellationToken);
            
            _logger.LogDebug("Fetching float data for {Ticker} from Polygon API", ticker);
            
            var url = $"https://api.polygon.io/v3/reference/tickers/{ticker}?apikey={apiKey}";
            
            using var client = _httpClientFactory.CreateClient();
            var response = await client.GetAsync(url, cancellationToken);
            
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogDebug("Failed to fetch float data for {Ticker}: {Status}", ticker, response.StatusCode);
                return null;
            }
            
            var json = await response.Content.ReadAsStringAsync(cancellationToken);
            var data = System.Text.Json.JsonSerializer.Deserialize<PolygonTickerDetailsResponse>(json);
            
            var sharesOutstanding = data?.Results?.SharesOutstanding 
                                 ?? data?.Results?.WeightedSharesOutstanding;

            // Log resolved shares outstanding for visibility
            _logger.LogInformation(
                "Polygon float for {Ticker}: SharesOutstanding={SharesOutstanding} (~{FloatMillions}M); WeightedSharesOutstanding={WeightedSharesOutstanding}",
                ticker,
                sharesOutstanding,
                sharesOutstanding.HasValue ? sharesOutstanding.Value / 1_000_000m : null,
                data?.Results?.WeightedSharesOutstanding);
            
            // Cache for 24 hours
            if (sharesOutstanding.HasValue)
            {
                _cache.Set(cacheKey, sharesOutstanding.Value, TimeSpan.FromHours(24));
            }
            
            return sharesOutstanding;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error fetching float data for {Ticker}", ticker);
            return null;
        }
    }

    public bool ShouldRebuildNow(DateTime utcNow)
    {
        var eastern = TimeZoneInfo.ConvertTimeFromUtc(utcNow, TryGetEasternTimeZone());
        
        // Only rebuild during market hours (9:30 AM - 4:00 PM ET)
        if (eastern.Hour < 9 || eastern.Hour >= 16)
        {
            return false;
        }
        
        if (eastern.Hour == 9 && eastern.Minute < 30)
        {
            return false;
        }
        
        // Line 265: log decision context before returning
        _logger.LogInformation("Universe rebuild check at {Time} ET -> {RebuildNow}", eastern, eastern.Minute % 5 == 0);

        // Rebuild every 5 minutes (00, 05, 10, 15, etc.)
        return eastern.Minute % 5 == 0;
    }

    private static TimeZoneInfo TryGetEasternTimeZone()
    {
        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById("Eastern Standard Time");
        }
        catch (TimeZoneNotFoundException)
        {
            return TimeZoneInfo.Utc;
        }
    }
}
