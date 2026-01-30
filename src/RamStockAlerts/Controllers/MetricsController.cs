using Microsoft.AspNetCore.Mvc;
using RamStockAlerts.Models;
using RamStockAlerts.Services;

namespace RamStockAlerts.Controllers;

[ApiController]
[Route("api/metrics")]
public sealed class MetricsController : ControllerBase
{
    private readonly IPerformanceMetricsAggregator _aggregator;

    public MetricsController(IPerformanceMetricsAggregator aggregator)
    {
        _aggregator = aggregator ?? throw new ArgumentNullException(nameof(aggregator));
    }

    [HttpGet]
    public async Task<ActionResult<PerformanceMetrics>> GetTodayMetrics(CancellationToken cancellationToken)
    {
        var metrics = await _aggregator.GetTodayMetrics(cancellationToken);
        return Ok(metrics ?? new PerformanceMetrics());
    }

    [HttpGet("history")]
    public async Task<ActionResult<List<OutcomeSummary>>> GetHistory([FromQuery] int days = 7, CancellationToken cancellationToken = default)
    {
        if (days <= 0)
        {
            return BadRequest("days must be >= 1");
        }

        var summaries = await _aggregator.GetHistoricalSummaries(days, cancellationToken);
        return Ok(summaries ?? new List<OutcomeSummary>());
    }
}
