using Microsoft.EntityFrameworkCore;
using RamStockAlerts.Data;
using RamStockAlerts.Models;

namespace RamStockAlerts.Services;

public class AlertThrottlingService
{
    private readonly AppDbContext _dbContext;
    private readonly ILogger<AlertThrottlingService> _logger;

    public AlertThrottlingService(AppDbContext dbContext, ILogger<AlertThrottlingService> logger)
    {
        _dbContext = dbContext;
        _logger = logger;
    }

    /// <summary>
    /// Check if a signal should be suppressed based on throttling rules.
    /// </summary>
    /// <param name="signal">The signal to check</param>
    /// <returns>True if the signal should be suppressed</returns>
    public async Task<bool> ShouldSuppressAsync(TradeSignal signal)
    {
        // Check 1: Same ticker within 10 minutes
        var tenMinutesAgo = DateTime.UtcNow.AddMinutes(-10);
        var recentSameTicker = await _dbContext.TradeSignals
            .Where(s => s.Ticker == signal.Ticker && s.Timestamp > tenMinutesAgo)
            .AnyAsync();

        if (recentSameTicker)
        {
            _logger.LogDebug(
                "Suppressing {Ticker}: duplicate within 10 minutes", 
                signal.Ticker);
            return true;
        }

        // Check 2: More than 3 alerts in last hour (any ticker)
        var oneHourAgo = DateTime.UtcNow.AddHours(-1);
        var hourlyCount = await _dbContext.TradeSignals
            .CountAsync(s => s.Timestamp > oneHourAgo);

        if (hourlyCount >= 3)
        {
            _logger.LogDebug(
                "Suppressing {Ticker}: exceeded hourly limit ({Count} signals)", 
                signal.Ticker, hourlyCount);
            return true;
        }

        return false;
    }
}
