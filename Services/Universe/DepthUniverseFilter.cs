using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Configuration;

namespace RamStockAlerts.Services.Universe;

public sealed class DepthUniverseFilter
{
    private readonly ContractClassificationService _classificationService;
    private readonly IConfiguration _configuration;
    private readonly ILogger<DepthUniverseFilter> _logger;
    private readonly HashSet<string> _etfLogged = new(StringComparer.OrdinalIgnoreCase);

    public DepthUniverseFilter(
        ContractClassificationService classificationService,
        IConfiguration configuration,
        ILogger<DepthUniverseFilter> logger)
    {
        _classificationService = classificationService;
        _configuration = configuration;
        _logger = logger;
    }

    public async Task<DepthUniverseFilterResult> FilterAsync(
        IReadOnlyList<string> universe,
        CancellationToken cancellationToken)
    {
        if (universe.Count == 0)
        {
            return new DepthUniverseFilterResult(universe, 0, 0, 0, 0, 0, 0);
        }

        var classifications = await _classificationService.GetClassificationsAsync(universe, cancellationToken);
        var filtered = new List<string>(universe.Count);
        var allowUnknown = _configuration.GetValue("Universe:AllowUnknownAsCommon", false);
        var counts = new ClassificationCounts();

        foreach (var symbol in universe)
        {
            if (string.IsNullOrWhiteSpace(symbol))
            {
                continue;
            }

            var normalized = symbol.Trim().ToUpperInvariant();
            classifications.TryGetValue(normalized, out var classification);
            var mapped = _classificationService.Classify(classification);
            counts.Increment(mapped);

            if (mapped == ContractSecurityClassification.Etf)
            {
                if (_etfLogged.Add(normalized))
                {
                    _logger.LogInformation("UniverseFilter exclude {Symbol} StockType=ETF", normalized);
                }

                continue;
            }

            if (mapped == ContractSecurityClassification.CommonStock ||
                (allowUnknown && mapped == ContractSecurityClassification.Unknown))
            {
                filtered.Add(normalized);
            }
        }

        return new DepthUniverseFilterResult(
            filtered,
            universe.Count,
            counts.CommonStock,
            counts.Etf,
            counts.Etn,
            counts.Unknown,
            counts.Other);
    }
}

public sealed record DepthUniverseFilterResult(
    IReadOnlyList<string> Filtered,
    int RawCount,
    int CommonCount,
    int EtfCount,
    int EtnCount,
    int UnknownCount,
    int OtherCount);

internal sealed class ClassificationCounts
{
    public int CommonStock { get; private set; }
    public int Etf { get; private set; }
    public int Etn { get; private set; }
    public int Unknown { get; private set; }
    public int Other { get; private set; }

    public void Increment(ContractSecurityClassification classification)
    {
        switch (classification)
        {
            case ContractSecurityClassification.CommonStock:
                CommonStock++;
                break;
            case ContractSecurityClassification.Etf:
                Etf++;
                break;
            case ContractSecurityClassification.Etn:
                Etn++;
                break;
            case ContractSecurityClassification.Unknown:
                Unknown++;
                break;
            default:
                Other++;
                break;
        }
    }
}
