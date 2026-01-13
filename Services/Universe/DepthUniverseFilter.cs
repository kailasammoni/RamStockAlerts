using Microsoft.Extensions.Logging;

namespace RamStockAlerts.Services.Universe;

public sealed class DepthUniverseFilter
{
    private readonly ContractClassificationService _classificationService;
    private readonly ILogger<DepthUniverseFilter> _logger;
    private readonly HashSet<string> _etfLogged = new(StringComparer.OrdinalIgnoreCase);

    public DepthUniverseFilter(
        ContractClassificationService classificationService,
        ILogger<DepthUniverseFilter> logger)
    {
        _classificationService = classificationService;
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
