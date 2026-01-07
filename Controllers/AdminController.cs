using Microsoft.AspNetCore.Mvc;
using RamStockAlerts.Feeds;
using RamStockAlerts.Services;

namespace RamStockAlerts.Controllers;

[ApiController]
[Route("admin")]
public class AdminController : ControllerBase
{
    private readonly ApiQuotaTracker _quotaTracker;
    private readonly AlpacaStreamClient _alpacaClient;
    private readonly ILogger<AdminController> _logger;

    public AdminController(
        ApiQuotaTracker quotaTracker, 
        AlpacaStreamClient alpacaClient,
        ILogger<AdminController> logger)
    {
        _quotaTracker = quotaTracker;
        _alpacaClient = alpacaClient;
        _logger = logger;
    }

    /// <summary>
    /// Get current API quota usage.
    /// </summary>
    [HttpGet("quota")]
    [ProducesResponseType(typeof(QuotaStatus), StatusCodes.Status200OK)]
    public IActionResult GetQuotaStatus()
    {
        var (minuteUtilization, dayUtilization) = _quotaTracker.GetUtilization();
        
        return Ok(new QuotaStatus
        {
            MinuteUtilizationPercent = minuteUtilization,
            DayUtilizationPercent = dayUtilization,
            CanMakeRequest = _quotaTracker.CanMakeRequest(),
            RequiredDelay = _quotaTracker.GetRequiredDelay()
        });
    }

    /// <summary>
    /// Get current latency statistics.
    /// </summary>
    [HttpGet("latency")]
    [ProducesResponseType(typeof(LatencyStats), StatusCodes.Status200OK)]
    public IActionResult GetLatencyStats()
    {
        var (avg, p50, p95, p99) = _alpacaClient.GetLatencyStats();
        
        return Ok(new LatencyStats
        {
            AverageMs = avg,
            MedianMs = p50,
            P95Ms = p95,
            P99Ms = p99,
            TargetMs = 500,
            IsWithinTarget = p95 < 500
        });
    }
}

public class QuotaStatus
{
    public double MinuteUtilizationPercent { get; set; }
    public double DayUtilizationPercent { get; set; }
    public bool CanMakeRequest { get; set; }
    public TimeSpan RequiredDelay { get; set; }
}

public class LatencyStats
{
    public double AverageMs { get; set; }
    public double MedianMs { get; set; }
    public double P95Ms { get; set; }
    public double P99Ms { get; set; }
    public double TargetMs { get; set; }
    public bool IsWithinTarget { get; set; }
}
