namespace RamStockAlerts.Controllers;

using Microsoft.AspNetCore.Mvc;
using RamStockAlerts.Controllers.Api.Execution;
using RamStockAlerts.Execution.Contracts;
using RamStockAlerts.Execution.Interfaces;
using RamStockAlerts.Models.Notifications;
using RamStockAlerts.Services;

/// <summary>
/// Controller for manual execution via REST API.
/// Provides endpoints to place orders/brackets and query execution ledger.
/// </summary>
[ApiController]
[Route("api/execution")]
public class ExecutionController : ControllerBase
{
    private readonly IExecutionService _executionService;
    private readonly IExecutionLedger _ledger;
    private readonly ILogger<ExecutionController> _logger;
    private readonly bool _executionEnabled;
    private readonly bool _isShadowMode;
    private readonly DiscordNotificationService? _discordNotificationService;

    public ExecutionController(
        IExecutionService executionService,
        IExecutionLedger ledger,
        ILogger<ExecutionController> logger,
        IConfiguration configuration,
        DiscordNotificationService? discordNotificationService = null)
    {
        _executionService = executionService ?? throw new ArgumentNullException(nameof(executionService));
        _ledger = ledger ?? throw new ArgumentNullException(nameof(ledger));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _executionEnabled = configuration.GetValue("Execution:Enabled", false);
        var tradingMode = configuration.GetValue<string>("TradingMode") ?? string.Empty;
        _isShadowMode = string.Equals(tradingMode, "Shadow", StringComparison.OrdinalIgnoreCase);
        _discordNotificationService = discordNotificationService;
    }

    /// <summary>
    /// Execute a single order.
    /// </summary>
    /// <param name="dto">Order intent details.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Execution result.</returns>
    [HttpPost("order")]
    [ProducesResponseType(typeof(RamStockAlerts.Execution.Contracts.ExecutionResult), 200)]
    [ProducesResponseType(400)]
    [ProducesResponseType(503)]
    public async Task<IActionResult> ExecuteOrder(
        [FromBody] OrderIntentDto dto,
        CancellationToken ct = default)
    {
        if (!_executionEnabled)
        {
            _logger.LogWarning("Execution endpoint called but Execution:Enabled=false");
            return StatusCode(503, new { error = "Execution module is disabled. Set Execution:Enabled=true in configuration." });
        }
        if (!ModelState.IsValid)
        {
            return BadRequest(ModelState);
        }

        try
        {
            var intent = dto.ToOrderIntent();
            _logger.LogInformation(
                "Executing order: {Symbol} {Side} {Quantity} @ {Type} (Mode: {Mode})",
                intent.Symbol, intent.Side, intent.Quantity, intent.Type, intent.Mode);

            var result = await _executionService.ExecuteAsync(intent, ct);

            _logger.LogInformation(
                "Order execution complete: {Status} (IntentId: {IntentId})",
                result.Status, intent.IntentId);

            TryNotifyExecutionStatus(intent, result);

            return Ok(result);
        }
        catch (ArgumentException ex)
        {
            _logger.LogWarning(ex, "Invalid order parameters");
            return BadRequest(new { error = ex.Message });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error executing order");
            return StatusCode(500, new { error = "Internal server error" });
        }
    }

    /// <summary>
    /// Execute a bracket order (entry + stop-loss + take-profit).
    /// </summary>
    /// <param name="dto">Bracket intent details.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Execution result.</returns>
    [HttpPost("bracket")]
    [ProducesResponseType(typeof(RamStockAlerts.Execution.Contracts.ExecutionResult), 200)]
    [ProducesResponseType(400)]
    [ProducesResponseType(503)]
    public async Task<IActionResult> ExecuteBracket(
        [FromBody] BracketIntentDto dto,
        CancellationToken ct = default)
    {
        if (!_executionEnabled)
        {
            _logger.LogWarning("Execution endpoint called but Execution:Enabled=false");
            return StatusCode(503, new { error = "Execution module is disabled. Set Execution:Enabled=true in configuration." });
        }

        if (!ModelState.IsValid)
        {
            return BadRequest(ModelState);
        }

        try
        {
            var intent = dto.ToBracketIntent();
            _logger.LogInformation(
                "Executing bracket: {Symbol} {Side} {Quantity} (Mode: {Mode}, HasStop: {HasStop}, HasTP: {HasTP})",
                intent.Entry.Symbol, intent.Entry.Side, intent.Entry.Quantity, 
                intent.Entry.Mode, intent.StopLoss != null, intent.TakeProfit != null);

            var result = await _executionService.ExecuteAsync(intent, ct);

            _logger.LogInformation(
                "Bracket execution complete: {Status} (IntentId: {IntentId})",
                result.Status, intent.Entry.IntentId);

            TryNotifyExecutionStatus(intent, result);

            return Ok(result);
        }
        catch (ArgumentException ex)
        {
            _logger.LogWarning(ex, "Invalid bracket parameters");
            return BadRequest(new { error = ex.Message });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error executing bracket");
            return StatusCode(500, new { error = "Internal server error" });
        }
    }

    /// <summary>
    /// Get recent execution ledger entries.
    /// </summary>
    /// <returns>Ledger data with recent intents and results.</returns>
    [HttpGet("ledger")]
    [ProducesResponseType(typeof(LedgerDto), 200)]
    [ProducesResponseType(503)]
    public IActionResult GetLedger()
    {
        if (!_executionEnabled)
        {
            _logger.LogWarning("Execution endpoint called but Execution:Enabled=false");
            return StatusCode(503, new { error = "Execution module is disabled. Set Execution:Enabled=true in configuration." });
        }

        try
        {
            var ledgerData = new LedgerDto
            {
                Intents = _ledger.GetIntents().ToList(),
                Brackets = _ledger.GetBrackets().ToList(),
                Results = _ledger.GetResults().ToList()
            };

            _logger.LogInformation(
                "Ledger query: {IntentCount} intents, {BracketCount} brackets, {ResultCount} results",
                ledgerData.Intents.Count, ledgerData.Brackets.Count, ledgerData.Results.Count);

            return Ok(ledgerData);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error retrieving ledger");
            return StatusCode(500, new { error = "Internal server error" });
        }
    }

    private void TryNotifyExecutionStatus(OrderIntent intent, ExecutionResult result)
    {
        if (_discordNotificationService == null || _isShadowMode || string.IsNullOrWhiteSpace(intent.Symbol))
        {
            return;
        }

        var status = MapExecutionStatus(result.Status);
        var details = BuildExecutionDetails(intent, result);

        _ = _discordNotificationService.SendExecutionStatusAsync(
            intent.Symbol,
            status,
            result.TimestampUtc,
            DiscordNotificationMode.Live,
            details);
    }

    private void TryNotifyExecutionStatus(BracketIntent intent, ExecutionResult result)
    {
        if (_discordNotificationService == null || _isShadowMode || string.IsNullOrWhiteSpace(intent.Entry.Symbol))
        {
            return;
        }

        var status = MapExecutionStatus(result.Status);
        var details = BuildExecutionDetails(intent.Entry, result);

        if (intent.StopLoss?.StopPrice != null)
        {
            details["StopLoss"] = intent.StopLoss.StopPrice.Value.ToString("F2");
        }

        if (intent.TakeProfit?.LimitPrice != null)
        {
            details["TakeProfit"] = intent.TakeProfit.LimitPrice.Value.ToString("F2");
        }

        _ = _discordNotificationService.SendExecutionStatusAsync(
            intent.Entry.Symbol,
            status,
            result.TimestampUtc,
            DiscordNotificationMode.Live,
            details);
    }

    private static Dictionary<string, string> BuildExecutionDetails(OrderIntent intent, ExecutionResult result)
    {
        var details = new Dictionary<string, string>
        {
            ["Side"] = intent.Side.ToString()
        };

        if (intent.Quantity.HasValue)
        {
            details["Quantity"] = intent.Quantity.Value.ToString("F0");
        }
        else if (intent.NotionalUsd.HasValue)
        {
            details["NotionalUsd"] = intent.NotionalUsd.Value.ToString("F2");
        }

        if (intent.LimitPrice.HasValue)
        {
            details["LimitPrice"] = intent.LimitPrice.Value.ToString("F2");
        }

        if (intent.StopPrice.HasValue)
        {
            details["StopPrice"] = intent.StopPrice.Value.ToString("F2");
        }

        if (!string.IsNullOrWhiteSpace(result.BrokerName))
        {
            details["Broker"] = result.BrokerName;
        }

        if (result.BrokerOrderIds.Count > 0)
        {
            details["BrokerOrderId"] = string.Join(", ", result.BrokerOrderIds);
        }

        if (!string.IsNullOrWhiteSpace(result.RejectionReason))
        {
            details["Reason"] = result.RejectionReason;
        }

        return details;
    }

    private static string MapExecutionStatus(ExecutionStatus status)
    {
        return status switch
        {
            ExecutionStatus.Accepted => "Submitted",
            ExecutionStatus.Submitted => "Submitted",
            ExecutionStatus.Filled => "Filled",
            ExecutionStatus.Cancelled => "Canceled",
            ExecutionStatus.Rejected => "Rejected",
            ExecutionStatus.Error => "Rejected",
            _ => status.ToString()
        };
    }
}
