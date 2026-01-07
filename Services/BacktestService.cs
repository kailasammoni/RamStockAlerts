using System.Text.Json;
using RamStockAlerts.Data;
using RamStockAlerts.Models;

namespace RamStockAlerts.Services;

/// <summary>
/// Backtest service for replaying historical events and validating signals.
/// </summary>
public class BacktestService
{
    private readonly IEventStore _eventStore;
    private readonly ILogger<BacktestService> _logger;

    public BacktestService(IEventStore eventStore, ILogger<BacktestService> logger)
    {
        _eventStore = eventStore;
        _logger = logger;
    }

    /// <summary>
    /// Replay events from the event store within a time range.
    /// </summary>
    public async Task<BacktestResult> ReplayAsync(
        DateTime startTime, 
        DateTime endTime, 
        double speedMultiplier = 1.0,
        CancellationToken cancellationToken = default)
    {
        var result = new BacktestResult
        {
            StartTime = startTime,
            EndTime = endTime,
            SpeedMultiplier = speedMultiplier
        };

        var events = new List<(string EventType, string Data, DateTime RecordedAt)>();
        
        await foreach (var evt in _eventStore.ReplayAsync(startTime, cancellationToken))
        {
            if (evt.RecordedAt > endTime)
                break;

            events.Add(evt);
            result.TotalEvents++;

            // Process different event types
            if (evt.EventType == "TradeSignalGenerated")
            {
                result.SignalsGenerated++;
            }
            else if (evt.EventType == "AlertSent")
            {
                result.AlertsSent++;
            }
        }

        result.Events = events;
        result.CompletedAt = DateTime.UtcNow;

        _logger.LogInformation(
            "Backtest completed: {Events} events, {Signals} signals, {Alerts} alerts from {Start} to {End}",
            result.TotalEvents, result.SignalsGenerated, result.AlertsSent, startTime, endTime);

        return result;
    }

    /// <summary>
    /// Calculate backtest performance metrics.
    /// </summary>
    public BacktestMetrics CalculateMetrics(BacktestResult result, List<TradeSignal> signals)
    {
        var metrics = new BacktestMetrics();

        if (!signals.Any())
            return metrics;

        var completedSignals = signals.Where(s => s.PnL.HasValue).ToList();
        
        if (completedSignals.Any())
        {
            var winners = completedSignals.Count(s => s.PnL > 0);
            metrics.WinRate = (decimal)winners / completedSignals.Count * 100;
            metrics.TotalSignals = completedSignals.Count;
            metrics.Winners = winners;
            metrics.Losers = completedSignals.Count - winners;

            var totalPnL = completedSignals.Sum(s => s.PnL ?? 0);
            metrics.TotalPnL = totalPnL;
            metrics.AveragePnL = totalPnL / completedSignals.Count;
        }

        return metrics;
    }
}

public class BacktestResult
{
    public DateTime StartTime { get; set; }
    public DateTime EndTime { get; set; }
    public double SpeedMultiplier { get; set; }
    public int TotalEvents { get; set; }
    public int SignalsGenerated { get; set; }
    public int AlertsSent { get; set; }
    public DateTime CompletedAt { get; set; }
    public List<(string EventType, string Data, DateTime RecordedAt)> Events { get; set; } = new();
}

public class BacktestMetrics
{
    public decimal WinRate { get; set; }
    public int TotalSignals { get; set; }
    public int Winners { get; set; }
    public int Losers { get; set; }
    public decimal TotalPnL { get; set; }
    public decimal AveragePnL { get; set; }
}
