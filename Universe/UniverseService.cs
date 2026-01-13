using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using RamStockAlerts.Services.Universe;

namespace RamStockAlerts.Universe;

public sealed class UniverseService
{
    private const string CacheKey = "universe.active.v2";
    private static readonly TimeSpan CacheTtl = TimeSpan.FromMinutes(5);

    private readonly IConfiguration _configuration;
    private readonly IMemoryCache _cache;
    private readonly ILogger<UniverseService> _logger;
    private readonly StaticUniverseSource _staticSource;
    private readonly IbkrScannerUniverseSource _scannerSource;
    private readonly DepthUniverseFilter _depthFilter;
    private readonly SemaphoreSlim _universeLock = new(1, 1);

    private IReadOnlyList<string> _lastUniverse = Array.Empty<string>();
    private string _lastSource = "unknown";

    public UniverseService(
        IConfiguration configuration,
        IMemoryCache cache,
        ILogger<UniverseService> logger,
        StaticUniverseSource staticSource,
        IbkrScannerUniverseSource scannerSource,
        DepthUniverseFilter depthFilter)
    {
        _configuration = configuration;
        _cache = cache;
        _logger = logger;
        _staticSource = staticSource;
        _scannerSource = scannerSource;
        _depthFilter = depthFilter;
    }

    public async Task<IReadOnlyList<string>> GetUniverseAsync(CancellationToken cancellationToken)
    {
        if (_cache.TryGetValue(CacheKey, out IReadOnlyList<string>? cached) && cached is not null)
        {
            return cached;
        }

        await _universeLock.WaitAsync(cancellationToken);
        try
        {
            if (_cache.TryGetValue(CacheKey, out cached) && cached is not null)
            {
                return cached;
            }

            var sourceLabel = ResolveSourceLabel();
            var sourceKey = sourceLabel.ToLowerInvariant();
            IReadOnlyList<string> universe;

            try
            {
                universe = sourceKey switch
                {
                    "static" => await _staticSource.GetUniverseAsync(cancellationToken),
                    "ibkrscanner" => await _scannerSource.GetUniverseAsync(cancellationToken),
                    _ => await _staticSource.GetUniverseAsync(cancellationToken)
                };

                if (sourceKey != "static" && sourceKey != "ibkrscanner")
                {
                    _logger.LogWarning(
                        "Universe source {Source} not recognized. Falling back to Static.",
                        sourceLabel);
                    sourceLabel = "Static";
                }
            }
            catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
            {
                if (sourceKey == "ibkrscanner" && _lastUniverse.Count > 0)
                {
                    _logger.LogWarning(
                        ex,
                        "IBKR scanner failed. Using cached universe from {Source} ({Count} symbols).",
                        _lastSource,
                        _lastUniverse.Count);
                    universe = _lastUniverse;
                    sourceLabel = _lastSource;
                }
                else
                {
                    _logger.LogError(ex, "Universe fetch failed for source {Source}.", sourceLabel);
                    universe = Array.Empty<string>();
                }
            }

            if (string.Equals(sourceKey, "ibkrscanner", StringComparison.OrdinalIgnoreCase))
            {
                universe = await _depthFilter.FilterAsync(universe, cancellationToken);
            }

            if (sourceKey == "ibkrscanner" && universe.Count == 0 && _lastUniverse.Count > 0)
            {
                _logger.LogWarning(
                    "IBKR scanner returned no symbols. Using cached universe from {Source} ({Count} symbols).",
                    _lastSource,
                    _lastUniverse.Count);
                universe = _lastUniverse;
                sourceLabel = _lastSource;
            }

            _cache.Set(CacheKey, universe, CacheTtl);

            if (universe.Count > 0)
            {
                _lastUniverse = universe;
                _lastSource = sourceLabel;
            }

            _logger.LogInformation(
                "Universe loaded from {Source} with {Count} symbols.",
                sourceLabel,
                universe.Count);

            return universe;
        }
        finally
        {
            _universeLock.Release();
        }
    }

    private string ResolveSourceLabel()
    {
        var source = _configuration["Universe:Source"];
        return string.IsNullOrWhiteSpace(source) ? "IbkrScanner" : source.Trim();
    }
}
