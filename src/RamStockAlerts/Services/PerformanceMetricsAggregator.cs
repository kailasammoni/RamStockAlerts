using System.Collections.Generic;
using System.Linq;
using RamStockAlerts.Models;

namespace RamStockAlerts.Services;

public interface IPerformanceMetricsAggregator
{
    Task<PerformanceMetrics> GetTodayMetrics(CancellationToken cancellationToken = default);
    Task<List<OutcomeSummary>> GetHistoricalSummaries(int days, CancellationToken cancellationToken = default);
}

public sealed class PerformanceMetricsAggregator : IPerformanceMetricsAggregator
{
    private readonly IOutcomeSummaryStore _outcomeStore;
    private readonly IPerformanceSnapshot? _snapshot;

    public PerformanceMetricsAggregator(IOutcomeSummaryStore outcomeStore, IPerformanceSnapshot? snapshot = null)
    {
        _outcomeStore = outcomeStore ?? throw new ArgumentNullException(nameof(outcomeStore));
        _snapshot = snapshot;
    }

    public async Task<PerformanceMetrics> GetTodayMetrics(CancellationToken cancellationToken = default)
    {
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var outcomes = await _outcomeStore.GetOutcomesByDateAsync(today, cancellationToken);

        if (outcomes.Count == 0 && _snapshot is not null)
        {
            return _snapshot.GetSnapshot();
        }

        var metrics = BuildPerformanceMetrics(outcomes);
        var drawdown = CalculateMaxDrawdown(outcomes);
        metrics.MaxDrawdownUsd = drawdown.maxDrawdownUsd;
        metrics.MaxDrawdownPercent = drawdown.maxDrawdownPercent;
        return metrics;
    }

    public async Task<List<OutcomeSummary>> GetHistoricalSummaries(int days, CancellationToken cancellationToken = default)
    {
        if (days < 1)
        {
            return new List<OutcomeSummary>();
        }

        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var startDate = today.AddDays(-(days - 1));
        var outcomes = await _outcomeStore.GetOutcomesByDateRangeAsync(startDate, today, cancellationToken);

        return outcomes
            .GroupBy(o => DateOnly.FromDateTime(o.OutcomeLabeledUtc.UtcDateTime))
            .OrderBy(g => g.Key)
            .Select(g => BuildOutcomeSummary(g.Key, g.ToList()))
            .ToList();
    }

    private static PerformanceMetrics BuildPerformanceMetrics(List<TradeOutcome> outcomes)
    {
        var metrics = new PerformanceMetrics();
        if (outcomes.Count == 0)
        {
            return metrics;
        }

        var winRMultiples = new List<decimal>();
        var lossRMultiples = new List<decimal>();

        foreach (var outcome in outcomes)
        {
            if (outcome is null)
            {
                continue;
            }

            switch (outcome.OutcomeType)
            {
                case "HitTarget":
                    metrics.WinCount++;
                    if (outcome.RiskMultiple.HasValue)
                    {
                        winRMultiples.Add(outcome.RiskMultiple.Value);
                    }
                    break;
                case "HitStop":
                    metrics.LossCount++;
                    if (outcome.RiskMultiple.HasValue)
                    {
                        lossRMultiples.Add(Math.Abs(outcome.RiskMultiple.Value));
                    }
                    break;
                case "NoExit":
                    metrics.OpenCount++;
                    break;
                case "NoHit":
                    metrics.NoHitCount++;
                    break;
            }

            if (outcome.PnlUsd.HasValue)
            {
                metrics.TotalPnlUsd += outcome.PnlUsd.Value;
            }
        }

        metrics.TotalSignals = metrics.WinCount + metrics.LossCount + metrics.OpenCount + metrics.NoHitCount;
        metrics.AvgWinRMultiple = winRMultiples.Count > 0 ? winRMultiples.Sum() / winRMultiples.Count : null;
        metrics.AvgLossRMultiple = lossRMultiples.Count > 0 ? lossRMultiples.Sum() / lossRMultiples.Count : null;
        return metrics;
    }

    private static OutcomeSummary BuildOutcomeSummary(DateOnly dateUtc, List<TradeOutcome> outcomes)
    {
        var summary = new OutcomeSummary
        {
            DateUtc = dateUtc,
            GeneratedUtc = DateTimeOffset.UtcNow,
            SchemaVersion = 1
        };

        foreach (var outcome in outcomes)
        {
            if (outcome is null)
            {
                continue;
            }

            summary.TotalSignals++;
            switch (outcome.OutcomeType)
            {
                case "HitTarget":
                    summary.HitTargetCount++;
                    summary.ClosedTradeCount++;
                    break;
                case "HitStop":
                    summary.HitStopCount++;
                    summary.ClosedTradeCount++;
                    break;
                case "NoExit":
                    summary.NoExitCount++;
                    break;
                case "NoHit":
                    summary.ClosedTradeCount++;
                    break;
            }

            if (outcome.PnlUsd.HasValue)
            {
                summary.TotalPnlUsd += outcome.PnlUsd.Value;
            }

            if (outcome.RiskMultiple.HasValue)
            {
                summary.SumRiskMultiples += outcome.RiskMultiple.Value;
            }
        }

        return summary;
    }

    private static (decimal? maxDrawdownUsd, decimal? maxDrawdownPercent) CalculateMaxDrawdown(List<TradeOutcome> outcomes)
    {
        if (outcomes.Count == 0)
        {
            return (null, null);
        }

        var ordered = outcomes
            .Where(o => o?.PnlUsd.HasValue == true)
            .OrderBy(o => o!.OutcomeLabeledUtc)
            .ToList();

        if (ordered.Count == 0)
        {
            return (null, null);
        }

        decimal equity = 0m;
        decimal peak = 0m;
        decimal maxDrawdown = 0m;

        foreach (var outcome in ordered)
        {
            equity += outcome!.PnlUsd!.Value;
            if (equity > peak)
            {
                peak = equity;
            }

            var drawdown = peak - equity;
            if (drawdown > maxDrawdown)
            {
                maxDrawdown = drawdown;
            }
        }

        var percent = peak > 0m ? maxDrawdown / peak : (decimal?)null;
        return (maxDrawdown, percent);
    }
}
