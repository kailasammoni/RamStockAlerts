using Microsoft.EntityFrameworkCore;
using RamStockAlerts.Data;
using RamStockAlerts.Models;
using Microsoft.Extensions.Logging;

namespace RamStockAlerts.Services;

public class PerformanceTracker
{
    private readonly AppDbContext _dbContext;
    private readonly ILogger<PerformanceTracker> _logger;

    public PerformanceTracker(AppDbContext dbContext, ILogger<PerformanceTracker> logger)
    {
        _dbContext = dbContext;
        _logger = logger;
    }

    public async Task<decimal?> GetWinRateAsync()
    {
        var filled = await _dbContext.TradeSignals
            .Where(s => s.Status == SignalStatus.Filled || s.Status == SignalStatus.Cancelled || s.Status == SignalStatus.Rejected)
            .ToListAsync();

        if (filled.Count == 0)
        {
            return null;
        }

        var wins = filled.Count(s => s.PnL.HasValue && s.PnL.Value > 0);
        return (decimal)wins / filled.Count;
    }

    public async Task<IDictionary<int, decimal>> GetHourlyEdgeAsync()
    {
        var signals = await _dbContext.TradeSignals
            .Where(s => s.Timestamp != default)
            .ToListAsync();

        return signals
            .GroupBy(s => s.Timestamp.Hour)
            .ToDictionary(g => g.Key, g => g.Average(s => s.PnL ?? 0m));
    }

    public async Task<TradeSignal?> GetOutcomeAsync(int id)
    {
        return await _dbContext.TradeSignals.FirstOrDefaultAsync(s => s.Id == id);
    }

    public async Task<decimal?> GetExpectationAsync()
    {
        var signals = await _dbContext.TradeSignals
            .Where(s => s.PnL.HasValue)
            .ToListAsync();

        if (!signals.Any()) return null;

        var wins = signals.Where(s => s.PnL > 0).ToList();
        var losses = signals.Where(s => s.PnL < 0).ToList();

        var winRate = signals.Count == 0 ? 0 : (decimal)wins.Count / signals.Count;
        var avgWin = wins.Any() ? wins.Average(s => s.PnL ?? 0m) : 0m;
        var avgLoss = losses.Any() ? losses.Average(s => s.PnL ?? 0m) : 0m;

        return (winRate * avgWin) + ((1 - winRate) * avgLoss);
    }
}
