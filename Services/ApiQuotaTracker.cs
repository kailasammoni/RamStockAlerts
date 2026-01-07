using System.Collections.Concurrent;

namespace RamStockAlerts.Services;

/// <summary>
/// Tracks API quota usage with token bucket algorithm for rate limiting.
/// </summary>
public class ApiQuotaTracker
{
    private readonly ILogger<ApiQuotaTracker> _logger;
    private readonly int _quotaPerMinute;
    private readonly int _quotaPerDay;
    
    private readonly ConcurrentQueue<DateTime> _requestTimestamps = new();
    private int _recentRequestCount;
    private int _dailyRequestCount;
    private DateTime _dailyResetTime;
    private readonly object _lock = new();

    public ApiQuotaTracker(IConfiguration configuration, ILogger<ApiQuotaTracker> logger)
    {
        _logger = logger;
        _quotaPerMinute = configuration.GetValue("Polygon:QuotaPerMinute", 5);
        _quotaPerDay = configuration.GetValue("Polygon:QuotaPerDay", 100);
        _dailyResetTime = DateTime.UtcNow.Date.AddDays(1);
    }

    /// <summary>
    /// Check if we can make a request without exceeding quota.
    /// </summary>
    public bool CanMakeRequest()
    {
        lock (_lock)
        {
            var now = DateTime.UtcNow;

            // Reset daily counter if needed
            if (now >= _dailyResetTime)
            {
                _dailyRequestCount = 0;
                _dailyResetTime = now.Date.AddDays(1);
                _logger.LogInformation("Daily quota reset. New reset time: {ResetTime}", _dailyResetTime);
            }

            // Check daily quota
            if (_dailyRequestCount >= _quotaPerDay)
            {
                _logger.LogWarning("Daily quota exceeded: {Count}/{Limit}", _dailyRequestCount, _quotaPerDay);
                return false;
            }

            // Remove timestamps older than 1 minute
            var oneMinuteAgo = now.AddMinutes(-1);
            while (_requestTimestamps.TryPeek(out var timestamp) && timestamp < oneMinuteAgo)
            {
                _requestTimestamps.TryDequeue(out _);
                _recentRequestCount--;
            }

            // Check per-minute quota
            if (_recentRequestCount >= _quotaPerMinute)
            {
                _logger.LogWarning("Per-minute quota exceeded: {Count}/{Limit}", 
                    _recentRequestCount, _quotaPerMinute);
                return false;
            }

            return true;
        }
    }

    /// <summary>
    /// Record a request for quota tracking.
    /// </summary>
    public void RecordRequest()
    {
        lock (_lock)
        {
            _requestTimestamps.Enqueue(DateTime.UtcNow);
            _recentRequestCount++;
            _dailyRequestCount++;
        }
    }

    /// <summary>
    /// Get current quota utilization percentage.
    /// </summary>
    public (double MinuteUtilization, double DayUtilization) GetUtilization()
    {
        lock (_lock)
        {
            var minuteUtilization = (double)_recentRequestCount / _quotaPerMinute * 100;
            var dayUtilization = (double)_dailyRequestCount / _quotaPerDay * 100;
            
            return (minuteUtilization, dayUtilization);
        }
    }

    /// <summary>
    /// Calculate delay needed before next request to stay within quota.
    /// </summary>
    public TimeSpan GetRequiredDelay()
    {
        lock (_lock)
        {
            if (_requestTimestamps.TryPeek(out var oldestTimestamp))
            {
                var oneMinuteAgo = DateTime.UtcNow.AddMinutes(-1);
                if (oldestTimestamp > oneMinuteAgo)
                {
                    // Need to wait until oldest request is older than 1 minute
                    return oldestTimestamp.AddMinutes(1) - DateTime.UtcNow;
                }
            }
            
            return TimeSpan.Zero;
        }
    }
}
