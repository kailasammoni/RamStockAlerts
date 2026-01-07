using Microsoft.EntityFrameworkCore;
using RamStockAlerts.Data;
using RamStockAlerts.Models;

namespace RamStockAlerts.Services;

public class SignalService
{
    private readonly AppDbContext _dbContext;
    private readonly MultiChannelNotificationService _notificationService;
    private readonly AlertThrottlingService _throttlingService;
    private readonly CircuitBreakerService _circuitBreakerService;
    private readonly IEventStore _eventStore;
    private readonly AlpacaTradingClient _tradingClient;
    private readonly ILogger<SignalService> _logger;

    public SignalService(
        AppDbContext dbContext,
        MultiChannelNotificationService notificationService,
        AlertThrottlingService throttlingService,
        CircuitBreakerService circuitBreakerService,
        IEventStore eventStore,
        AlpacaTradingClient tradingClient,
        ILogger<SignalService> logger)
    {
        _dbContext = dbContext;
        _notificationService = notificationService;
        _throttlingService = throttlingService;
        _circuitBreakerService = circuitBreakerService;
        _eventStore = eventStore;
        _tradingClient = tradingClient;
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

        // Store event for backtest replay
        await _eventStore.AppendAsync("TradeSignalGenerated", new
        {
            signal.Id,
            signal.Ticker,
            signal.Entry,
            signal.Stop,
            signal.Target,
            signal.Score,
            signal.Timestamp
        });

        _logger.LogInformation(
            "Saved signal for {Ticker}: Entry={Entry}, Score={Score}",
            signal.Ticker, signal.Entry, signal.Score);

        // Attempt auto-trading if enabled
        signal.AutoTradingAttempted = true;
        try
        {
            var orderId = await _tradingClient.PlaceBracketOrderAsync(signal);
            if (orderId != null)
            {
                signal.OrderId = orderId;
                signal.OrderPlacedAt = DateTime.UtcNow;
                signal.Status = SignalStatus.Filled; // Mark as filled since order placed
                
                await _eventStore.AppendAsync("OrderPlaced", new
                {
                    signal.Id,
                    signal.Ticker,
                    OrderId = orderId,
                    signal.Entry,
                    signal.Stop,
                    signal.Target,
                    PositionSize = signal.PositionSize
                });
            }
            else
            {
                signal.AutoTradingSkipReason = "Auto-trading disabled or conditions not met";
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Auto-trading failed for {Ticker}", signal.Ticker);
            signal.AutoTradingSkipReason = $"Error: {ex.Message}";
        }

        // Update signal with order info
        await _dbContext.SaveChangesAsync();

        // Send multi-channel notification with failover
        try
        {
            var sent = await _notificationService.SendWithFailoverAsync(signal);
            if (sent)
            {
                await _eventStore.AppendAsync("AlertSent", new
                {
                    signal.Id,
                    signal.Ticker
                });
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to send notification for {Ticker}", signal.Ticker);
            // Don't fail the save if notification fails
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
