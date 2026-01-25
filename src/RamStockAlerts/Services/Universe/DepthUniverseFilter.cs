using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Configuration;

namespace RamStockAlerts.Services.Universe;

public sealed class DepthUniverseFilter
{
    private readonly ContractClassificationService _classificationService;
    private readonly IConfiguration _configuration;
    private readonly ILogger<DepthUniverseFilter> _logger;
    private readonly HashSet<string> _skipLogged = new(StringComparer.OrdinalIgnoreCase);
    private static readonly HashSet<string> AllowedExchanges = new(StringComparer.OrdinalIgnoreCase) { "NYSE", "NASDAQ" };

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

            var eligibility = EvaluateEligibility(normalized, classification, mapped);
            if (!eligibility.IsEligible)
            {
                LogOnce(normalized, eligibility.Reason);
                continue;
            }

            filtered.Add(normalized);
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

    private static (bool IsEligible, string Reason) EvaluateEligibility(
        string symbol,
        ContractClassification? classification,
        ContractSecurityClassification mapped)
    {
        if (classification is null)
        {
            return (false, "MissingClassification");
        }

        if (classification.ConId <= 0)
        {
            return (false, "MissingConId");
        }

        if (!IsStockSecType(classification.SecType))
        {
            return (false, $"SecTypeNotStock:{classification.SecType ?? "null"}");
        }

        if (!IsCommonStockType(classification.StockType, mapped))
        {
            return (false, $"StockTypeNotCommon:{classification.StockType ?? "null"}");
        }

        if (string.IsNullOrWhiteSpace(classification.PrimaryExchange))
        {
            return (false, "MissingPrimaryExchange");
        }

        if (!AllowedExchanges.Contains(classification.PrimaryExchange.Trim().ToUpperInvariant()))
        {
            return (false, $"PrimaryExchangeNotAllowed:{classification.PrimaryExchange}");
        }

        return (true, string.Empty);
    }

    private static bool IsStockSecType(string? secType)
    {
        return !string.IsNullOrWhiteSpace(secType)
               && secType.Trim().Equals("STK", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsCommonStockType(
        string? stockType,
        ContractSecurityClassification mapped)
    {
        if (string.IsNullOrWhiteSpace(stockType))
        {
            return false;
        }

        var normalized = stockType.Trim();
        if (normalized.Equals("COMMON", StringComparison.OrdinalIgnoreCase)
            || normalized.Equals("CS", StringComparison.OrdinalIgnoreCase)
            || normalized.Equals("STK", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }
        return mapped == ContractSecurityClassification.CommonStock;
    }

    private void LogOnce(string symbol, string reason)
    {
        var key = $"{symbol}:{reason}";
        if (_skipLogged.Add(key))
        {
            _logger.LogInformation("UniverseFilter exclude {Symbol} reason={Reason}", symbol, reason);
        }
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
