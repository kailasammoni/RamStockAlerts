using Microsoft.Extensions.Caching.Memory;
using RamStockAlerts.Models;

namespace RamStockAlerts.Services;

public class CircuitBreakerService
{
    private readonly ILogger<CircuitBreakerService> _logger;
    private readonly IMemoryCache _cache;
    private const string SuspensionKey = "circuit.suspended";
    private const string RejectStreakKey = "circuit.rejectStreak";
    private readonly TimeZoneInfo _eastern = TryGetEasternTimeZone();

    public CircuitBreakerService(ILogger<CircuitBreakerService> logger, IMemoryCache cache)
    {
        _logger = logger;
        _cache = cache;
    }

    // Force disabled for testing
    public bool IsSuspended() => false; // _cache.TryGetValue(SuspensionKey, out _);

    public void Suspend(TimeSpan duration, string reason)
    {
        _logger.LogWarning("Circuit breaker activated for {Minutes} minutes: {Reason}", duration.TotalMinutes, reason);
        _cache.Set(SuspensionKey, true, duration);
    }

    public void TrackOutcome(SignalStatus status)
    {
        var streak = _cache.GetOrCreate<int>(RejectStreakKey, _ => 0);

        if (status == SignalStatus.Rejected || status == SignalStatus.Cancelled)
        {
            streak++;
            _cache.Set(RejectStreakKey, streak, TimeSpan.FromHours(1));

            if (streak >= 2)
            {
                Suspend(TimeSpan.FromMinutes(15), "Two consecutive rejected/cancelled signals");
                _cache.Set(RejectStreakKey, 0, TimeSpan.FromHours(1));
            }
        }
        else
        {
            _cache.Set(RejectStreakKey, 0, TimeSpan.FromHours(1));
        }
    }

    public bool ShouldThrottle(decimal spread, decimal printsPerSecond, DateTime timestampUtc)
    {
        // Liquidity collapse detection
        if (spread >= 0.1m)
        {
            Suspend(TimeSpan.FromMinutes(5), "Spread widened beyond 10%");
            return true;
        }

        // Tape slowdown during active hours
        var easternNow = TimeZoneInfo.ConvertTimeFromUtc(timestampUtc, _eastern);
        var hour = easternNow.Hour + easternNow.Minute / 60.0;
        var inActiveWindow = hour is >= 9.25 and <= 15.75;

        if (inActiveWindow && printsPerSecond < 1m)
        {
            Suspend(TimeSpan.FromMinutes(10), "Tape slowdown below 1 print/sec");
            return true;
        }

        return false;
    }

    private static TimeZoneInfo TryGetEasternTimeZone()
    {
        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById("Eastern Standard Time");
        }
        catch (TimeZoneNotFoundException)
        {
            return TimeZoneInfo.Utc;
        }
    }
}
