using Microsoft.AspNetCore.Mvc;
using RamStockAlerts.Services;

namespace RamStockAlerts.Controllers;

[ApiController]
[Route("admin")]
public class AdminController : ControllerBase
{
    private readonly ApiQuotaTracker _quotaTracker;
    private readonly ILogger<AdminController> _logger;

    public AdminController(ApiQuotaTracker quotaTracker, ILogger<AdminController> logger)
    {
        _quotaTracker = quotaTracker;
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
}

public class QuotaStatus
{
    public double MinuteUtilizationPercent { get; set; }
    public double DayUtilizationPercent { get; set; }
    public bool CanMakeRequest { get; set; }
    public TimeSpan RequiredDelay { get; set; }
}
