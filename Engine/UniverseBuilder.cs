using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using RamStockAlerts.Feeds;
using RamStockAlerts.Models.Polygon;

namespace RamStockAlerts.Engine;

/// <summary>
/// Tracks market phase for appropriate universe filtering strategy.
/// </summary>
public enum MarketPhase
{
    BeforePreMarket,  // Before 9:00 AM ET
    PreMarket,        // 9:00-9:30 AM ET (real-time pre-market data from Alpaca)
    RegularMarket,    // 9:30 AM-4:00 PM ET (regular trading hours)
    AfterMarketClose  // After 4:00 PM ET
}

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
        var enablePreMarketFiltering = _configuration.GetValue("Universe:EnablePreMarketFiltering", true);
        var preMarketStartHour = _configuration.GetValue("Universe:PreMarketStartHour", 9);
        var preMarketMinTrades = _configuration.GetValue("Universe:PreMarketMinTrades", 5);

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

        // Determine current phase
        var currentPhase = GetMarketPhase(enablePreMarketFiltering);
        if (currentPhase == MarketPhase.BeforePreMarket || currentPhase == MarketPhase.AfterMarketClose)
        {
            _logger.LogInformation("Not rebuilding universe - market phase: {Phase}", currentPhase);
            return Array.Empty<string>();
        }

        // Fetch live tickers from Polygon API (market movers endpoint)
        var apiKey = _configuration["Polygon:ApiKey"];
        if (string.IsNullOrEmpty(apiKey))
        {
            _logger.LogError("Polygon API key not configured and strict mode is on. No universe will be built.");
            return Array.Empty<string>();
        }

        try
        {
            // Step 1: Get broad candidate list from Polygon
            var candidates = await GetCandidateTickersAsync(apiKey, cancellationToken);
            if (!candidates.Any())
            {
                _logger.LogWarning("No candidate tickers fetched from Polygon");
                return Array.Empty<string>();
            }

            _logger.LogInformation("Fetched {CandidateCount} candidates from Polygon", candidates.Count);

            // Step 2: Filter based on market phase
            IReadOnlyCollection<string> filtered = currentPhase switch
            {
                MarketPhase.PreMarket => FilterWithPreMarketData(
                    candidates,
                    minPrice,
                    maxPrice,
                    maxSpread,
                    preMarketMinTrades,
                    maxTickers),
                MarketPhase.RegularMarket => await FilterWithMarketDataAsync(
                    candidates,
                    minPrice,
                    maxPrice,
                    minRelVol,
                    maxSpread,
                    maxFloat,
                    maxTickers,
                    cancellationToken),
                _ => Array.Empty<string>()
            };

            _logger.LogInformation(
                "Universe built with {Count} symbols (Phase: {Phase}, Price ${Min}-${Max}, Spread<{Spread}, excluding ETFs/ADRs/OTC) | Top: {TopTickers}",
                filtered.Count,
                currentPhase,
                minPrice,
                maxPrice,
                maxSpread,
                string.Join(", ", filtered.Take(10)));

            return filtered;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error building universe from market data");
            return Array.Empty<string>();
        }
    }

    /// <summary>
    /// Get broad candidate list from Polygon API.
    /// Cached for 1 hour since list doesn't change often.
    /// </summary>
    private async Task<List<string>> GetCandidateTickersAsync(string apiKey, CancellationToken cancellationToken)
    {
        var cacheKey = "universe.candidates";
        
        if (_cache.TryGetValue(cacheKey, out List<string>? cached) && cached is not null)
        {
            _logger.LogDebug("Using cached candidate list ({Count} tickers)", cached.Count);
            return cached;
        }

        try
        {
            // Use Polygon Ticker endpoint to search for US stocks
            // /v3/reference/tickers?market=stocks&type=CS filters for Common Stocks only (excludes ETFs, ADRs, warrants)
            var url = $"https://api.polygon.io/v3/reference/tickers?market=stocks&type=CS&active=true&exchange=XNAS&limit=1000&apikey={apiKey}";
            
            using var client = _httpClientFactory.CreateClient();
            var response = await client.GetAsync(url, cancellationToken);
            
            if (!response.IsSuccessStatusCode)
            {
                var errorBody = await response.Content.ReadAsStringAsync(cancellationToken);
                _logger.LogError("Failed to fetch tickers from Polygon: {Status} - {Error}", response.StatusCode, errorBody);
                return new List<string>();
            }

            var json = await response.Content.ReadAsStringAsync(cancellationToken);
            var tickerData = System.Text.Json.JsonSerializer.Deserialize<PolygonTickerListResponse>(json);

            if (tickerData?.Results == null || !tickerData.Results.Any())
            {
                _logger.LogWarning("No ticker data returned from Polygon");
                return new List<string>();
            }

            // Filter by basic validation: exclude OTC and preferred shares
            var candidates = tickerData.Results
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
                .ToList();

            // Cache for 1 hour
            _cache.Set(cacheKey, candidates, TimeSpan.FromHours(1));

            return candidates;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error fetching candidate tickers from Polygon");
            return new List<string>();
        }
    }

    /// <summary>
    /// Filter candidates using real-time pre-market data from Alpaca (9:00-9:25 AM ET).
    /// This gives us TODAY'S actual conditions, not yesterday's stale data.
    /// </summary>
    private List<string> FilterWithPreMarketData(
        List<string> candidates,
        decimal minPrice,
        decimal maxPrice,
        decimal maxSpread,
        int preMarketMinTrades,
        int maxTickers)
    {
        var filtered = new List<string>();
        int candidatesWithData = 0;

        foreach (var ticker in candidates)
        {
            // Check if we have pre-market data for this ticker
            var state = _alpacaClient.GetSymbolState(ticker);
            if (state == null)
            {
                _logger.LogDebug("{Ticker} filtered: No pre-market data yet", ticker);
                continue;
            }

            candidatesWithData++;

            // Filter by price (minPrice to maxPrice)
            var price = state.GetCurrentPrice();
            if (!price.HasValue || price.Value < minPrice || price.Value > maxPrice)
            {
                if (price.HasValue)
                    _logger.LogDebug("{Ticker} filtered: Price ${Price} outside range ${Min}-${Max}", ticker, price.Value, minPrice, maxPrice);
                continue;
            }

            // Filter by spread (< maxSpread)
            var spread = state.GetCurrentSpread();
            if (!spread.HasValue || spread.Value > maxSpread)
            {
                if (spread.HasValue)
                    _logger.LogDebug("{Ticker} filtered: Spread {Spread:P2} exceeds max {MaxSpread:P2}", ticker, spread.Value, maxSpread);
                continue;
            }

            // Filter by pre-market activity (at least N trades)
            // Note: We access _recentTrades via reflection since it's internal
            var recentTradesProperty = state.GetType().GetField("_recentTrades", 
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            if (recentTradesProperty == null)
            {
                _logger.LogDebug("{Ticker}: Cannot access trade data for pre-market filtering", ticker);
                continue;
            }

            var recentTrades = recentTradesProperty.GetValue(state) as dynamic;
            var tradeCount = recentTrades?.Count ?? 0;
            if (tradeCount < preMarketMinTrades)
            {
                _logger.LogDebug("{Ticker} filtered: Only {TradeCount} trades, need {MinTrades}", ticker, tradeCount, preMarketMinTrades);
                continue;
            }

            filtered.Add(ticker);

            if (filtered.Count >= maxTickers)
                break;
        }

        _logger.LogInformation(
            "Pre-market universe filter: {CandidateCount} candidates, {WithDataCount} with data, {PassedCount} passed filters",
            candidates.Count,
            candidatesWithData,
            filtered.Count);

        return filtered;
    }

    /// <summary>
    /// Filter candidates using market data from Alpaca (9:30+ AM ET).
    /// More restrictive than pre-market: adds relative volume and float checks.
    /// </summary>
    private async Task<List<string>> FilterWithMarketDataAsync(
        List<string> candidates,
        decimal minPrice,
        decimal maxPrice,
        decimal minRelVol,
        decimal maxSpread,
        decimal maxFloat,
        int maxTickers,
        CancellationToken cancellationToken)
    {
        var filtered = new List<string>();

        foreach (var ticker in candidates)
        {
            // Check if we have market data for this ticker
            var state = _alpacaClient.GetSymbolState(ticker);
            if (state == null)
            {
                _logger.LogDebug("{Ticker} filtered: No real-time data available", ticker);
                continue;
            }

            // Filter by price (minPrice to maxPrice)
            var price = state.GetCurrentPrice();
            if (!price.HasValue || price.Value < minPrice || price.Value > maxPrice)
            {
                if (price.HasValue)
                    _logger.LogDebug("{Ticker} filtered: Price ${Price} outside range ${Min}-${Max}", ticker, price.Value, minPrice, maxPrice);
                continue;
            }

            // Filter by spread (< maxSpread)
            var spread = state.GetCurrentSpread();
            if (!spread.HasValue || spread.Value > maxSpread)
            {
                if (spread.HasValue)
                    _logger.LogDebug("{Ticker} filtered: Spread {Spread:P2} exceeds max {MaxSpread:P2}", ticker, spread.Value, maxSpread);
                continue;
            }

            // Filter by relative volume (> minRelVol)
            var relVol = state.GetRelativeVolume();
            if (!relVol.HasValue || relVol.Value < minRelVol)
            {
                if (relVol.HasValue)
                    _logger.LogDebug("{Ticker} filtered: RelVol {RelVol:F2} below min {MinRelVol}", ticker, relVol.Value, minRelVol);
                continue;
            }

            // Filter by float (shares outstanding < maxFloat)
            var sharesOutstanding = await GetSharesOutstandingAsync(ticker, cancellationToken);
            if (sharesOutstanding.HasValue && sharesOutstanding.Value > maxFloat)
            {
                _logger.LogDebug("{Ticker} filtered: float {Float}M exceeds max {MaxFloat}M",
                    ticker, sharesOutstanding.Value / 1_000_000m, maxFloat / 1_000_000m);
                continue;
            }

            filtered.Add(ticker);

            if (filtered.Count >= maxTickers)
                break;
        }

        return filtered;
    }

    /// <summary>
    /// Determine the current market phase based on Eastern Time.
    /// </summary>
    private MarketPhase GetMarketPhase(bool enablePreMarketFiltering)
    {
        var eastern = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, TryGetEasternTimeZone());

        // After 4:00 PM ET
        if (eastern.Hour >= 16)
            return MarketPhase.AfterMarketClose;

        // 9:30 AM to 4:00 PM ET
        if (eastern.Hour > 9 || (eastern.Hour == 9 && eastern.Minute >= 30))
            return MarketPhase.RegularMarket;

        // 9:00 AM to 9:30 AM ET (pre-market)
        if (enablePreMarketFiltering && eastern.Hour == 9 && eastern.Minute >= 0)
            return MarketPhase.PreMarket;

        // Before 9:00 AM ET
        return MarketPhase.BeforePreMarket;
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
        
        // Don't rebuild after market close (after 4:00 PM ET)
        if (eastern.Hour >= 16)
        {
            return false;
        }
        
        // START REBUILDING AT 9:00 AM (not 9:30)
        // This allows progressive refinement as more pre-market data arrives
        if (eastern.Hour < 9)
        {
            return false;
        }
        
        // Rebuild every 5 minutes (00, 05, 10, 15, etc.)
        var shouldRebuild = eastern.Minute % 5 == 0;
        
        _logger.LogInformation("Universe rebuild check at {Time} ET -> {RebuildNow}", eastern, shouldRebuild);

        return shouldRebuild;
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
