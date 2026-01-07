using Microsoft.AspNetCore.Mvc;
using RamStockAlerts.Data;
using RamStockAlerts.Models;
using RamStockAlerts.Services;

namespace RamStockAlerts.Controllers;

[ApiController]
[Route("api/[controller]")]
public class SignalsController : ControllerBase
{
    private readonly SignalService _signalService;
    private readonly PerformanceTracker _performanceTracker;
    private readonly IEventStore _eventStore;
    private readonly ILogger<SignalsController> _logger;

    public SignalsController(
        SignalService signalService, 
        PerformanceTracker performanceTracker, 
        IEventStore eventStore,
        ILogger<SignalsController> logger)
    {
        _signalService = signalService;
        _performanceTracker = performanceTracker;
        _eventStore = eventStore;
        _logger = logger;
    }

    /// <summary>
    /// Create a new trade signal.
    /// </summary>
    [HttpPost]
    [ProducesResponseType(typeof(TradeSignal), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status429TooManyRequests)]
    public async Task<IActionResult> CreateSignal([FromBody] CreateSignalRequest request)
    {
        if (!ModelState.IsValid)
        {
            return BadRequest(ModelState);
        }

        var signal = new TradeSignal
        {
            Ticker = request.Ticker.ToUpperInvariant(),
            Entry = request.Entry,
            Stop = request.Stop,
            Target = request.Target,
            Score = request.Score,
            Timestamp = request.Timestamp ?? DateTime.UtcNow
        };

        var result = await _signalService.SaveSignalAsync(signal);

        if (result == null)
        {
            return StatusCode(StatusCodes.Status429TooManyRequests, 
                new { message = "Signal suppressed by throttling rules" });
        }

        return Ok(result);
    }

    /// <summary>
    /// Get recent trade signals.
    /// </summary>
    [HttpGet]
    [ProducesResponseType(typeof(List<TradeSignal>), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetSignals([FromQuery] int count = 10)
    {
        var signals = await _signalService.GetRecentSignalsAsync(count);
        return Ok(signals);
    }

    /// <summary>
    /// Get signals for a specific ticker.
    /// </summary>
    [HttpGet("{ticker}")]
    [ProducesResponseType(typeof(List<TradeSignal>), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetSignalsByTicker(string ticker, [FromQuery] int count = 10)
    {
        var signals = await _signalService.GetSignalsByTickerAsync(ticker.ToUpperInvariant(), count);
        return Ok(signals);
    }

    [HttpGet("analytics/winrate")]
    [ProducesResponseType(typeof(decimal?), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetWinRate()
    {
        var winRate = await _performanceTracker.GetWinRateAsync();
        return Ok(winRate);
    }

    [HttpGet("analytics/by-hour")]
    [ProducesResponseType(typeof(IDictionary<int, decimal>), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetByHour()
    {
        var edge = await _performanceTracker.GetHourlyEdgeAsync();
        return Ok(edge);
    }

    [HttpGet("{id}/outcome")]
    [ProducesResponseType(typeof(TradeSignal), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetOutcome(int id)
    {
        var signal = await _performanceTracker.GetOutcomeAsync(id);
        return signal is null ? NotFound() : Ok(signal);
    }

    /// <summary>
    /// Replay events from the event store for debugging and backtesting.
    /// </summary>
    [HttpGet("events/replay")]
    [ProducesResponseType(typeof(List<EventReplayDto>), StatusCodes.Status200OK)]
    public async Task<IActionResult> ReplayEvents(
        [FromQuery] DateTime? from = null,
        [FromQuery] int limit = 100,
        CancellationToken cancellationToken = default)
    {
        var events = new List<EventReplayDto>();
        var count = 0;

        await foreach (var evt in _eventStore.ReplayAsync(from, cancellationToken))
        {
            events.Add(new EventReplayDto
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

public class CreateSignalRequest
{
    public required string Ticker { get; set; }
    public decimal Entry { get; set; }
    public decimal Stop { get; set; }
    public decimal Target { get; set; }
    public decimal Score { get; set; }
    public DateTime? Timestamp { get; set; }
}

public class EventReplayDto
{
    public string EventType { get; set; } = string.Empty;
    public string Data { get; set; } = string.Empty;
    public DateTime RecordedAt { get; set; }
}
