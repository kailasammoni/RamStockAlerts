using Microsoft.EntityFrameworkCore;
using RamStockAlerts.Data;
using RamStockAlerts.Models;

namespace RamStockAlerts.Services;

public class SignalService
{
    private readonly AppDbContext _dbContext;
    private readonly DiscordNotificationService _discordService;
    private readonly AlertThrottlingService _throttlingService;
    private readonly CircuitBreakerService _circuitBreakerService;
    private readonly ILogger<SignalService> _logger;

    public SignalService(
        AppDbContext dbContext,
        DiscordNotificationService discordService,
        AlertThrottlingService throttlingService,
        CircuitBreakerService circuitBreakerService,
        ILogger<SignalService> logger)
    {
        _dbContext = dbContext;
        _discordService = discordService;
        _throttlingService = throttlingService;
        _circuitBreakerService = circuitBreakerService;
        _logger = logger;
    }

    public async Task<TradeSignal?> SaveSignalAsync(TradeSignal signal)
    {
        if (_circuitBreakerService.IsSuspended())
        {
            _logger.LogWarning("Circuit breaker active; signal for {Ticker} skipped", signal.Ticker);
            return null;
        }

        // Check throttling first
        if (await _throttlingService.ShouldSuppressAsync(signal))
        {
            _logger.LogInformation(
                "Signal for {Ticker} suppressed by throttling", 
                signal.Ticker);
            _circuitBreakerService.TrackOutcome(SignalStatus.Rejected);
            return null;
        }

        // Save to database
        _dbContext.TradeSignals.Add(signal);
        await _dbContext.SaveChangesAsync();

        _logger.LogInformation(
            "Saved signal for {Ticker}: Entry={Entry}, Score={Score}",
            signal.Ticker, signal.Entry, signal.Score);

        // Send Discord notification
        try
        {
            await _discordService.SendSignalAsync(signal);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to send Discord notification for {Ticker}", signal.Ticker);
            // Don't fail the save if Discord fails
        }

        _circuitBreakerService.TrackOutcome(signal.Status);

        return signal;
    }

    public async Task<List<TradeSignal>> GetRecentSignalsAsync(int count = 10)
    {
        return await _dbContext.TradeSignals
            .OrderByDescending(s => s.Timestamp)
            .Take(count)
            .ToListAsync();
    }

    public async Task<List<TradeSignal>> GetSignalsByTickerAsync(string ticker, int count = 10)
    {
        return await _dbContext.TradeSignals
            .Where(s => s.Ticker == ticker)
            .OrderByDescending(s => s.Timestamp)
            .Take(count)
            .ToListAsync();
    }
}
