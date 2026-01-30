using RamStockAlerts.Models;

namespace RamStockAlerts.Services;

public interface IPerformanceSnapshot
{
    void RecordOutcome(TradeOutcome outcome);
    PerformanceMetrics GetSnapshot();
}

public sealed class DailyPerformanceSnapshot : IPerformanceSnapshot
{
    private readonly object _lock = new();
    private DateOnly _currentDate = DateOnly.FromDateTime(DateTime.UtcNow);
    private int _winCount;
    private int _lossCount;
    private int _openCount;
    private int _noHitCount;
    private decimal _totalPnlUsd;
    private decimal _sumWinRMultiples;
    private decimal _sumLossRMultiples;
    private int _winRCount;
    private int _lossRCount;

    public void RecordOutcome(TradeOutcome outcome)
    {
        if (outcome is null)
        {
            return;
        }

        lock (_lock)
        {
            ResetIfNewDay();

            switch (outcome.OutcomeType)
            {
                case "HitTarget":
                    _winCount++;
                    if (outcome.RiskMultiple.HasValue)
                    {
                        _sumWinRMultiples += outcome.RiskMultiple.Value;
                        _winRCount++;
                    }
                    break;
                case "HitStop":
                    _lossCount++;
                    if (outcome.RiskMultiple.HasValue)
                    {
                        _sumLossRMultiples += Math.Abs(outcome.RiskMultiple.Value);
                        _lossRCount++;
                    }
                    break;
                case "NoExit":
                    _openCount++;
                    break;
                case "NoHit":
                    _noHitCount++;
                    break;
            }

            if (outcome.PnlUsd.HasValue)
            {
                _totalPnlUsd += outcome.PnlUsd.Value;
            }
        }
    }

    public PerformanceMetrics GetSnapshot()
    {
        lock (_lock)
        {
            ResetIfNewDay();

            return new PerformanceMetrics
            {
                WinCount = _winCount,
                LossCount = _lossCount,
                OpenCount = _openCount,
                NoHitCount = _noHitCount,
                TotalSignals = _winCount + _lossCount + _openCount + _noHitCount,
                TotalPnlUsd = _totalPnlUsd,
                AvgWinRMultiple = _winRCount > 0 ? _sumWinRMultiples / _winRCount : null,
                AvgLossRMultiple = _lossRCount > 0 ? _sumLossRMultiples / _lossRCount : null
            };
        }
    }

    private void ResetIfNewDay()
    {
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        if (today != _currentDate)
        {
            _currentDate = today;
            _winCount = 0;
            _lossCount = 0;
            _openCount = 0;
            _noHitCount = 0;
            _totalPnlUsd = 0m;
            _sumWinRMultiples = 0m;
            _sumLossRMultiples = 0m;
            _winRCount = 0;
            _lossRCount = 0;
        }
    }
}
