using RamStockAlerts.Models;

namespace RamStockAlerts.Services;

/// <summary>
/// Phase 3.4: Public tape status wrapper for data quality evaluation.
/// </summary>
public enum TapeStatusKind
{
    MissingSubscription,
    NotWarmedUp,
    Stale,
    Ready
}

/// <summary>
/// Phase 3.4: Public tape status data for data quality evaluation.
/// </summary>
public sealed record TapeStatusData(
    TapeStatusKind Kind,
    long? AgeMs = null,
    int TradesInWarmupWindow = 0,
    int WarmupMinTrades = 0,
    int WarmupWindowMs = 0);

/// <summary>
/// Phase 3.4: Centralized data quality flag detection and interpretation.
/// Handles: PartialBook, StaleTick (TapeStale), DepthStale, and other quality flags.
/// </summary>
public interface IDataQualityValidator
{
    /// <summary>
    /// Builds data quality flags for a given snapshot.
    /// </summary>
    IReadOnlyList<string> BuildDataQualityFlags(
        OrderBookState book,
        DepthSnapshot depthSnapshot,
        TapeStats tapeStats,
        TapeStatusData tapeStatus,
        long nowMs);

    /// <summary>
    /// Interprets a data quality flag and returns its severity and recommended action.
    /// </summary>
    DataQualityFlagInterpretation InterpretFlag(string flag);

    /// <summary>
    /// Determines if the given flags indicate a critical data quality issue.
    /// </summary>
    bool HasCriticalIssues(IReadOnlyList<string> flags);
}

/// <summary>
/// Interpretation of a data quality flag.
/// </summary>
public sealed record DataQualityFlagInterpretation(
    string Flag,
    DataQualitySeverity Severity,
    string Description,
    string? RecommendedAction);

/// <summary>
/// Severity levels for data quality issues.
/// </summary>
public enum DataQualitySeverity
{
    /// <summary>Informational only, no action needed</summary>
    Info = 0,
    
    /// <summary>Warning - may affect signal quality but not critical</summary>
    Warning = 1,
    
    /// <summary>Critical - should prevent signal acceptance or trigger retry</summary>
    Critical = 2
}

public sealed class DataQualityValidator : IDataQualityValidator
{
    private const int StaleDepthThresholdMs = 2000;

    public IReadOnlyList<string> BuildDataQualityFlags(
        OrderBookState book,
        DepthSnapshot depthSnapshot,
        TapeStats tapeStats,
        TapeStatusData tapeStatus,
        long nowMs)
    {
        var flags = new List<string>();

        // Book validity check
        if (!book.IsBookValid(out var reason, nowMs))
        {
            flags.Add($"BookInvalid:{reason}");
        }

        // Tape status checks
        switch (tapeStatus.Kind)
        {
            case TapeStatusKind.MissingSubscription:
                flags.Add("TapeMissingSubscription");
                break;
            
            case TapeStatusKind.NotWarmedUp:
                flags.Add("TapeNotWarmedUp");
                flags.Add($"TapeNotWarmedUp:tradesInWindow={tapeStatus.TradesInWarmupWindow}");
                flags.Add($"TapeNotWarmedUp:warmupMinTrades={tapeStatus.WarmupMinTrades}");
                flags.Add($"TapeNotWarmedUp:warmupWindowMs={tapeStatus.WarmupWindowMs}");
                if (tapeStatus.AgeMs.HasValue)
                {
                    flags.Add($"TapeLastAgeMs={tapeStatus.AgeMs.Value}");
                }
                break;
            
            case TapeStatusKind.Stale:
                // StaleTick: tape data is stale
                flags.Add("TapeStale");
                flags.Add("StaleTick"); // Alias for compatibility
                if (tapeStatus.AgeMs.HasValue)
                {
                    flags.Add($"TapeStale:ageMs={tapeStatus.AgeMs.Value}");
                }
                break;
        }

        // PartialBook: less than expected depth levels available
        if (depthSnapshot.BidsTopN.Count < depthSnapshot.ExpectedDepthLevels || 
            depthSnapshot.AsksTopN.Count < depthSnapshot.ExpectedDepthLevels)
        {
            flags.Add("PartialBook");
            flags.Add($"PartialBook:bidLevels={depthSnapshot.BidsTopN.Count}");
            flags.Add($"PartialBook:askLevels={depthSnapshot.AsksTopN.Count}");
            flags.Add($"PartialBook:expected={depthSnapshot.ExpectedDepthLevels}");
        }

        // StaleDepth: depth data is older than threshold
        if (depthSnapshot.LastDepthUpdateAgeMs.HasValue && 
            depthSnapshot.LastDepthUpdateAgeMs.Value > StaleDepthThresholdMs)
        {
            flags.Add("StaleDepth");
            flags.Add($"StaleDepth:ageMs={depthSnapshot.LastDepthUpdateAgeMs.Value}");
        }

        return flags;
    }

    public DataQualityFlagInterpretation InterpretFlag(string flag)
    {
        if (string.IsNullOrWhiteSpace(flag))
        {
            return new DataQualityFlagInterpretation(
                flag ?? "null",
                DataQualitySeverity.Info,
                "Unknown flag",
                null);
        }

        // Extract base flag name (before colon)
        var baseFlag = flag.Split(':')[0];

        return baseFlag switch
        {
            "PartialBook" => new DataQualityFlagInterpretation(
                flag,
                DataQualitySeverity.Critical,
                "Depth book is incomplete - fewer levels than expected",
                "Trigger PartialBook retry via MarketDataSubscriptionManager.HandlePartialBookAsync()"),

            "StaleTick" or "TapeStale" => new DataQualityFlagInterpretation(
                flag,
                DataQualitySeverity.Critical,
                "Tape data is stale - no recent trades within freshness window",
                "Reject signal or wait for fresh tape data"),

            "StaleDepth" => new DataQualityFlagInterpretation(
                flag,
                DataQualitySeverity.Warning,
                "Depth data is stale - last update exceeded age threshold",
                "Monitor for depth refresh or consider rejecting signal"),

            "TapeNotWarmedUp" => new DataQualityFlagInterpretation(
                flag,
                DataQualitySeverity.Warning,
                "Tape has insufficient trade history within warmup window",
                "Add to tape warmup watchlist for periodic re-evaluation"),

            "TapeMissingSubscription" => new DataQualityFlagInterpretation(
                flag,
                DataQualitySeverity.Critical,
                "Tape subscription is missing - cannot evaluate tape freshness",
                "Ensure symbol is subscribed to tape data"),

            "BookInvalid" => new DataQualityFlagInterpretation(
                flag,
                DataQualitySeverity.Critical,
                "Order book is invalid - missing bid/ask or stale data",
                "Wait for valid book state or reject signal"),

            "TapeLastAgeMs" => new DataQualityFlagInterpretation(
                flag,
                DataQualitySeverity.Info,
                "Informational: age of last tape tick in milliseconds",
                null),

            "HeartbeatNoDecision" => new DataQualityFlagInterpretation(
                flag,
                DataQualitySeverity.Info,
                "Heartbeat entry - no trading decision made",
                null),

            "MissingBookContext" => new DataQualityFlagInterpretation(
                flag,
                DataQualitySeverity.Warning,
                "Book context is missing from journal entry",
                "Ensure depth snapshots are captured correctly"),

            _ => new DataQualityFlagInterpretation(
                flag,
                DataQualitySeverity.Info,
                "Unknown or informational flag",
                null)
        };
    }

    public bool HasCriticalIssues(IReadOnlyList<string> flags)
    {
        if (flags == null || flags.Count == 0)
        {
            return false;
        }

        foreach (var flag in flags)
        {
            var interpretation = InterpretFlag(flag);
            if (interpretation.Severity == DataQualitySeverity.Critical)
            {
                return true;
            }
        }

        return false;
    }
}

/// <summary>
/// Phase 3.4: Depth snapshot for data quality evaluation.
/// </summary>
public sealed record DepthSnapshot(
    IReadOnlyList<(decimal Price, decimal Size)> BidsTopN,
    IReadOnlyList<(decimal Price, decimal Size)> AsksTopN,
    int ExpectedDepthLevels,
    long? LastDepthUpdateAgeMs);

/// <summary>
/// Phase 3.4: Tape statistics for data quality evaluation.
/// </summary>
public sealed record TapeStats(
    decimal VelocityTps,
    int TradesInWindow,
    long? LastTradeAgeMs);
