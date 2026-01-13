using Microsoft.Extensions.Logging;

namespace RamStockAlerts.Services.Universe;

public sealed class DepthUniverseFilter
{
    private readonly ContractClassificationService _classificationService;
    private readonly DepthEligibilityCache _eligibilityCache;
    private readonly ILogger<DepthUniverseFilter> _logger;
    private readonly HashSet<string> _etfLogged = new(StringComparer.OrdinalIgnoreCase);

    public DepthUniverseFilter(
        ContractClassificationService classificationService,
        DepthEligibilityCache eligibilityCache,
        ILogger<DepthUniverseFilter> logger)
    {
        _classificationService = classificationService;
        _eligibilityCache = eligibilityCache;
        _logger = logger;
    }

    public async Task<IReadOnlyList<string>> FilterAsync(
        IReadOnlyList<string> universe,
        CancellationToken cancellationToken)
    {
        if (universe.Count == 0)
        {
            return universe;
        }

        var classifications = await _classificationService.GetClassificationsAsync(universe, cancellationToken);
        var filtered = new List<string>(universe.Count);
        var now = DateTimeOffset.UtcNow;

        foreach (var symbol in universe)
        {
            if (string.IsNullOrWhiteSpace(symbol))
            {
                continue;
            }

            var normalized = symbol.Trim().ToUpperInvariant();
            classifications.TryGetValue(normalized, out var classification);

            if (IsEtf(classification))
            {
                if (_etfLogged.Add(normalized))
                {
                    _logger.LogInformation("UniverseFilter exclude {Symbol} StockType=ETF", normalized);
                }

                continue;
            }

            if (!_eligibilityCache.CanRequestDepth(classification, normalized, now, out var state))
            {
                _eligibilityCache.LogSkipOnce(classification, normalized, state);
                continue;
            }

            filtered.Add(normalized);
        }

        return filtered;
    }

    private static bool IsEtf(ContractClassification? classification)
    {
        if (classification is null || string.IsNullOrWhiteSpace(classification.StockType))
        {
            return false;
        }

        return classification.StockType.Equals("ETF", StringComparison.OrdinalIgnoreCase);
    }
}
