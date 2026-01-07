using Microsoft.AspNetCore.Mvc;
using RamStockAlerts.Data;
using RamStockAlerts.Services;

namespace RamStockAlerts.Controllers;

[ApiController]
[Route("api/backtest")]
public class BacktestController : ControllerBase
{
    private readonly BacktestService _backtestService;
    private readonly IEventStore _eventStore;
    private readonly ILogger<BacktestController> _logger;

    public BacktestController(
        BacktestService backtestService,
        IEventStore eventStore,
        ILogger<BacktestController> logger)
    {
        _backtestService = backtestService;
        _eventStore = eventStore;
        _logger = logger;
    }

    /// <summary>
    /// Replay events from the event store within a time range.
    /// </summary>
    [HttpPost("replay")]
    [ProducesResponseType(typeof(BacktestResult), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> Replay([FromBody] ReplayRequest request, CancellationToken cancellationToken)
    {
        if (request.StartTime >= request.EndTime)
        {
            return BadRequest("StartTime must be before EndTime");
        }

        if (request.SpeedMultiplier <= 0 || request.SpeedMultiplier > 1000)
        {
            return BadRequest("SpeedMultiplier must be between 0 and 1000");
        }

        var result = await _backtestService.ReplayAsync(
            request.StartTime,
            request.EndTime,
            request.SpeedMultiplier,
            cancellationToken);

        return Ok(result);
    }

    /// <summary>
    /// Get events from the event store.
    /// </summary>
    [HttpGet("events")]
    [ProducesResponseType(typeof(List<EventDto>), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetEvents(
        [FromQuery] DateTime? from = null,
        [FromQuery] int limit = 100,
        CancellationToken cancellationToken = default)
    {
        var events = new List<EventDto>();
        var count = 0;

        await foreach (var evt in _eventStore.ReplayAsync(from, cancellationToken))
        {
            events.Add(new EventDto
            {
                EventType = evt.EventType,
                Data = evt.Data,
                RecordedAt = evt.RecordedAt
            });

            count++;
            if (count >= limit)
                break;
        }

        return Ok(events);
    }
}

public class ReplayRequest
{
    public DateTime StartTime { get; set; }
    public DateTime EndTime { get; set; }
    public double SpeedMultiplier { get; set; } = 1.0;
}

public class EventDto
{
    public string EventType { get; set; } = string.Empty;
    public string Data { get; set; } = string.Empty;
    public DateTime RecordedAt { get; set; }
}
