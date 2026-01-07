using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using RamStockAlerts.Feeds;

namespace RamStockAlerts.Engine;

public class UniverseBuilder
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IConfiguration _configuration;
    private readonly IMemoryCache _cache;
    private readonly ILogger<UniverseBuilder> _logger;

    private const string UniverseCacheKey = "universe.active";
    private static readonly TimeSpan UniverseTtl = TimeSpan.FromHours(24);

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

        // Placeholder: in MVP, fall back to configured tickers while filters are wired in
        var fallback = _configuration.GetSection("Polygon:Tickers").Get<string[]>() ?? Array.Empty<string>();
        if (!fallback.Any())
        {
            _logger.LogWarning("Universe fallback is empty; add Polygon:Tickers or configure Universe feed.");
        }

        // TODO: fetch live market snapshot to enforce filters once provider is wired
        var filtered = fallback.Where(t => !string.IsNullOrWhiteSpace(t)).Select(t => t.Trim().ToUpperInvariant()).Distinct().ToList();

        _logger.LogInformation("Universe built with {Count} symbols (price {Min}-{Max}, relVol>{RelVol}, spread<{Spread}, float<{Float})", filtered.Count, minPrice, maxPrice, minRelVol, maxSpread, maxFloat);

        return filtered;
    }

    public bool ShouldRebuildNow(DateTime utcNow)
    {
        var eastern = TimeZoneInfo.ConvertTimeFromUtc(utcNow, TryGetEasternTimeZone());
        return eastern.Hour == 9 && eastern.Minute >= 25 && eastern.Minute < 30;
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
