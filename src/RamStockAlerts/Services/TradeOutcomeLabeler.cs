using RamStockAlerts.Models;

namespace RamStockAlerts.Services;

/// <summary>
/// Interface for labeling outcomes of accepted trade signals.
/// </summary>
public interface ITradeOutcomeLabeler
{
    /// <summary>
    /// Label an outcome for an accepted signal based on entry/exit prices and time.
    /// </summary>
    /// <param name="journalEntry">The accepted journal entry containing blueprint and decision info.</param>
    /// <param name="exitPrice">The exit price (from execution API or manual entry).</param>
    /// <param name="exitTime">The time of exit (UTC).</param>
    /// <returns>Labeled TradeOutcome with risk multiple, P&L, win flag, etc.</returns>
    Task<TradeOutcome> LabelOutcomeAsync(
        TradeJournalEntry journalEntry,
        decimal? exitPrice,
        DateTimeOffset? exitTime,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Batch label outcomes for multiple accepted entries.
    /// </summary>
    Task<List<TradeOutcome>> LabelOutcomesAsync(
        List<TradeJournalEntry> acceptedEntries,
        Dictionary<Guid, (decimal exitPrice, DateTimeOffset exitTime)>? exitData = null,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Implements trade outcome labeling: assigns entry/stop/target, calculates risk multiple,
/// P&L, and win flag based on exit price.
/// </summary>
public sealed class TradeOutcomeLabeler : ITradeOutcomeLabeler
{
    private readonly Serilog.ILogger _logger = Serilog.Log.ForContext<TradeOutcomeLabeler>();

    public async Task<TradeOutcome> LabelOutcomeAsync(
        TradeJournalEntry journalEntry,
        decimal? exitPrice,
        DateTimeOffset? exitTime,
        CancellationToken cancellationToken = default)
    {
        if (journalEntry == null)
        {
            throw new ArgumentNullException(nameof(journalEntry));
        }

        var outcome = new TradeOutcome
        {
            DecisionId = journalEntry.DecisionId,
            Symbol = journalEntry.Symbol,
            Direction = journalEntry.Direction,
            SchemaVersion = 1,
            OutcomeLabeledUtc = DateTimeOffset.UtcNow,
            QualityFlags = journalEntry.DataQualityFlags ?? new List<string>()
        };

        // Extract blueprint prices
        var blueprint = journalEntry.Blueprint;
        if (blueprint != null)
        {
            outcome.EntryPrice = blueprint.Entry;
            outcome.StopPrice = blueprint.Stop;
            outcome.TargetPrice = blueprint.Target;
        }

        // Calculate duration and exit flags
        if (journalEntry.DecisionTimestampUtc.HasValue && exitTime.HasValue)
        {
            outcome.DurationSeconds = (long)(exitTime.Value - journalEntry.DecisionTimestampUtc.Value).TotalSeconds;
        }

        outcome.ExitPrice = exitPrice;

        // Determine outcome type
        if (!exitPrice.HasValue)
        {
            outcome.OutcomeType = "NoExit";
        }
        else if (outcome.TargetPrice.HasValue && IsTargetHit(exitPrice.Value, outcome.TargetPrice.Value, journalEntry.Direction))
        {
            outcome.OutcomeType = "HitTarget";
        }
        else if (outcome.StopPrice.HasValue && IsStopHit(exitPrice.Value, outcome.StopPrice.Value, journalEntry.Direction))
        {
            outcome.OutcomeType = "HitStop";
        }
        else
        {
            outcome.OutcomeType = "NoHit";
        }

        // Calculate P&L, risk multiple, and win flag
        if (exitPrice.HasValue && outcome.EntryPrice.HasValue)
        {
            var isLong = journalEntry.Direction?.Equals("Long", StringComparison.OrdinalIgnoreCase) == true;
            var priceMove = exitPrice.Value - outcome.EntryPrice.Value;
            var rawPnl = isLong ? priceMove : -priceMove;

            var shareCount = journalEntry.Blueprint?.ShareCount ?? 1;
            outcome.PnlUsd = rawPnl * shareCount;

            // Calculate risk multiple
            if (outcome.StopPrice.HasValue && outcome.EntryPrice.Value != outcome.StopPrice.Value)
            {
                var riskRange = Math.Abs(outcome.EntryPrice.Value - outcome.StopPrice.Value);

                if (riskRange > 0)
                {
                    outcome.RiskMultiple = rawPnl / riskRange;
                }
            }

            // Determine win
            outcome.IsWin = rawPnl > 0;
        }

        _logger.Information(
            "Labeled outcome for {DecisionId}: {Symbol} {Direction} @ {Entry} → {Exit} | {Type} | R={RiskMultiple} | PnL=${PnL}",
            outcome.DecisionId, outcome.Symbol, outcome.Direction,
            outcome.EntryPrice, outcome.ExitPrice,
            outcome.OutcomeType, outcome.RiskMultiple, outcome.PnlUsd);

        return await Task.FromResult(outcome);
    }

    public async Task<List<TradeOutcome>> LabelOutcomesAsync(
        List<TradeJournalEntry> acceptedEntries,
        Dictionary<Guid, (decimal exitPrice, DateTimeOffset exitTime)>? exitData = null,
        CancellationToken cancellationToken = default)
    {
        if (acceptedEntries == null)
        {
            throw new ArgumentNullException(nameof(acceptedEntries));
        }

        var outcomes = new List<TradeOutcome>();
        exitData ??= new Dictionary<Guid, (decimal, DateTimeOffset)>();

        foreach (var entry in acceptedEntries)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var exitPrice = exitData.TryGetValue(entry.DecisionId, out var exitInfo) ? exitInfo.exitPrice : (decimal?)null;
            var exitTime = exitData.TryGetValue(entry.DecisionId, out var exitInfo2) ? exitInfo2.exitTime : (DateTimeOffset?)null;

            var outcome = await LabelOutcomeAsync(entry, exitPrice, exitTime, cancellationToken);
            outcomes.Add(outcome);
        }

        _logger.Information("Labeled {Count} outcomes from {Total} accepted entries", outcomes.Count, acceptedEntries.Count);
        return outcomes;
    }

    /// <summary>
    /// Check if exit price has reached or exceeded the target.
    /// </summary>
    private static bool IsTargetHit(decimal exitPrice, decimal targetPrice, string? direction)
    {
        return direction?.Equals("Long", StringComparison.OrdinalIgnoreCase) == true
            ? exitPrice >= targetPrice
            : exitPrice <= targetPrice;
    }

    /// <summary>
    /// Check if exit price has hit or breached the stop.
    /// </summary>
    private static bool IsStopHit(decimal exitPrice, decimal stopPrice, string? direction)
    {
        return direction?.Equals("Long", StringComparison.OrdinalIgnoreCase) == true
            ? exitPrice <= stopPrice
            : exitPrice >= stopPrice;
    }

    /// <summary>
    /// Determine if the trade is a win (profit in the direction).
    /// </summary>
    private static bool IsWin(decimal pnl, string? direction)
    {
        return direction?.Equals("Long", StringComparison.OrdinalIgnoreCase) == true
            ? pnl > 0
            : pnl < 0;
    }
}
