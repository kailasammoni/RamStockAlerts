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

    private const string UniverseCacheKey = "universe.active";
    private static readonly TimeSpan UniverseTtl = TimeSpan.FromMinutes(5);

    // TODO: Replace IMemoryCache with Redis for distributed caching with 5-minute TTL

    public UniverseBuilder(
        IHttpClientFactory httpClientFactory,
        IConfiguration configuration,
        IMemoryCache cache,
        ILogger<UniverseBuilder> logger)
    {
        _httpClientFactory = httpClientFactory;
        _configuration = configuration;
        _cache = cache;
        _logger = logger;
    }

    public async Task<IReadOnlyCollection<string>> GetActiveUniverseAsync(CancellationToken cancellationToken)
    {
        if (_cache.TryGetValue(UniverseCacheKey, out IReadOnlyCollection<string>? cached) && cached is not null)
        {
            return cached;
        }

        var universe = await BuildUniverseAsync(cancellationToken);
        _cache.Set(UniverseCacheKey, universe, UniverseTtl);
        return universe;
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
            // /v3/reference/tickers?market=stocks&active=true&limit=1000
            // This gets us a list of active US stock tickers, we filter by our criteria
            var url = $"https://api.polygon.io/v3/reference/tickers?market=stocks&active=true&limit=250&sort=updated&apikey={apiKey}";
            
            using var client = _httpClientFactory.CreateClient();
            var response = await client.GetAsync(url, cancellationToken);
            
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("Failed to fetch tickers from Polygon: {Status}", response.StatusCode);
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
            // Exclude ETFs, ADRs, OTC, preferred shares, and halted symbols
            var filtered = tickerData.Results
                .Where(t => 
                    // Basic validation
                    t.Ticker != null &&
                    !string.IsNullOrWhiteSpace(t.Ticker) &&
                    
                    // Type filters: Common Stock only (CS), exclude ETFs, ADRs
                    t.Type == "CS" &&
                    
                    // Exclude OTC (have dot notation like "STOCK.OTC")
                    !t.Ticker.Contains('.') &&
                    
                    // Exclude preferred shares (have dash like "STOCK-A")
                    !t.Ticker.Contains('-') &&
                    
                    // TODO: Filter by price range (10-80) - requires real-time price data
                    // TODO: Filter by relative volume > 2 - requires volume comparison
                    // TODO: Filter by spread < 0.05 - requires bid/ask data
                    // TODO: Filter by float < 150M - requires shares outstanding data
                    // TODO: Exclude halted symbols - requires trading status check
                    
                    true // Placeholder until data sources available
                )
                .Select(t => t.Ticker!.ToUpperInvariant())
                .Distinct()
                .Take(maxTickers)
                .ToList();

            _logger.LogInformation(
                "Universe built with {Count} symbols (Price ${Min}-${Max}, RelVol>{RelVol}, Spread<{Spread}, Float<{Float}M, excluding ETFs/ADRs/OTC/halted) | Top: {TopTickers}",
                filtered.Count, 
                minPrice, 
                maxPrice,
                minRelVol,
                maxSpread,
                maxFloat / 1_000_000,
                string.Join(", ", filtered.Take(10)));

            if (!filtered.Any())
            {
                _logger.LogWarning("No tickers passed filters; falling back to hardcoded list");
                var fallback = _configuration.GetSection("Polygon:Tickers").Get<string[]>() ?? Array.Empty<string>();
                return fallback.Where(t => !string.IsNullOrWhiteSpace(t)).Select(t => t.Trim().ToUpperInvariant()).Distinct().ToList();
            }

            return filtered;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error building universe from market data; falling back to hardcoded tickers");
            var fallback = _configuration.GetSection("Polygon:Tickers").Get<string[]>() ?? Array.Empty<string>();
            return fallback.Where(t => !string.IsNullOrWhiteSpace(t)).Select(t => t.Trim().ToUpperInvariant()).Distinct().ToList();
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
