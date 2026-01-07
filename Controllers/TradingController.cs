using Microsoft.AspNetCore.Mvc;
using RamStockAlerts.Services;

namespace RamStockAlerts.Controllers;

[ApiController]
[Route("api/[controller]")]
public class TradingController : ControllerBase
{
    private readonly AlpacaTradingClient _tradingClient;
    private readonly ILogger<TradingController> _logger;

    public TradingController(
        AlpacaTradingClient tradingClient,
        ILogger<TradingController> logger)
    {
        _tradingClient = tradingClient;
        _logger = logger;
    }

    /// <summary>
    /// Get Alpaca account information.
    /// </summary>
    [HttpGet("account")]
    [ProducesResponseType(typeof(AccountDto), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetAccount()
    {
        var account = await _tradingClient.GetAccountAsync();
        if (account == null)
        {
            return NotFound("Unable to fetch account information");
        }

        return Ok(new AccountDto
        {
            AccountNumber = account.AccountNumber ?? "",
            Cash = account.TradableCash,
            BuyingPower = account.BuyingPower,
            Equity = account.Equity,
            PortfolioValue = account.Equity,
            IsPaperTrading = account.IsTradingBlocked
        });
    }

    /// <summary>
    /// Get all open positions.
    /// </summary>
    [HttpGet("positions")]
    [ProducesResponseType(typeof(List<PositionDto>), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetPositions()
    {
        var positions = await _tradingClient.GetAllPositionsAsync();
        
        var dtos = positions.Select(p => new PositionDto
        {
            Symbol = p.Symbol,
            Quantity = p.Quantity,
            AvgEntryPrice = p.AverageEntryPrice,
            CurrentPrice = p.AssetCurrentPrice,
            MarketValue = p.MarketValue,
            UnrealizedPnL = p.UnrealizedProfitLoss,
            UnrealizedPnLPercent = p.UnrealizedProfitLossPercent
        }).ToList();

        return Ok(dtos);
    }

    /// <summary>
    /// Get position for a specific ticker.
    /// </summary>
    [HttpGet("positions/{ticker}")]
    [ProducesResponseType(typeof(PositionDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetPosition(string ticker)
    {
        var position = await _tradingClient.GetPositionAsync(ticker.ToUpperInvariant());
        if (position == null)
        {
            return NotFound($"No position found for {ticker}");
        }

        return Ok(new PositionDto
        {
            Symbol = position.Symbol,
            Quantity = position.Quantity,
            AvgEntryPrice = position.AverageEntryPrice,
            CurrentPrice = position.AssetCurrentPrice,
            MarketValue = position.MarketValue,
            UnrealizedPnL = position.UnrealizedProfitLoss,
            UnrealizedPnLPercent = position.UnrealizedProfitLossPercent
        });
    }

    /// <summary>
    /// Get order status.
    /// </summary>
    [HttpGet("orders/{orderId}")]
    [ProducesResponseType(typeof(OrderDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetOrder(Guid orderId)
    {
        var order = await _tradingClient.GetOrderAsync(orderId);
        if (order == null)
        {
            return NotFound($"Order {orderId} not found");
        }

        return Ok(new OrderDto
        {
            OrderId = order.OrderId.ToString(),
            Symbol = order.Symbol,
            Quantity = order.Quantity,
            FilledQuantity = order.FilledQuantity,
            OrderType = order.OrderType.ToString(),
            Side = order.OrderSide.ToString(),
            Status = order.OrderStatus.ToString(),
            LimitPrice = order.LimitPrice,
            StopPrice = order.StopPrice,
            SubmittedAt = order.CreatedAtUtc,
            FilledAt = order.FilledAtUtc
        });
    }

    /// <summary>
    /// Cancel an order.
    /// </summary>
    [HttpDelete("orders/{orderId}")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> CancelOrder(Guid orderId)
    {
        var success = await _tradingClient.CancelOrderAsync(orderId);
        if (!success)
        {
            return BadRequest($"Failed to cancel order {orderId}");
        }

        return Ok(new { message = $"Order {orderId} cancelled" });
    }
}

public class AccountDto
{
    public string AccountNumber { get; set; } = string.Empty;
    public decimal? Cash { get; set; }
    public decimal? BuyingPower { get; set; }
    public decimal? Equity { get; set; }
    public decimal? PortfolioValue { get; set; }
    public bool IsPaperTrading { get; set; }
}

public class PositionDto
{
    public string Symbol { get; set; } = string.Empty;
    public decimal Quantity { get; set; }
    public decimal AvgEntryPrice { get; set; }
    public decimal? CurrentPrice { get; set; }
    public decimal? MarketValue { get; set; }
    public decimal? UnrealizedPnL { get; set; }
    public decimal? UnrealizedPnLPercent { get; set; }
}

public class OrderDto
{
    public string OrderId { get; set; } = string.Empty;
    public string Symbol { get; set; } = string.Empty;
    public decimal? Quantity { get; set; }
    public decimal? FilledQuantity { get; set; }
    public string OrderType { get; set; } = string.Empty;
    public string Side { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public decimal? LimitPrice { get; set; }
    public decimal? StopPrice { get; set; }
    public DateTime? SubmittedAt { get; set; }
    public DateTime? FilledAt { get; set; }
}
