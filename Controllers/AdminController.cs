using Microsoft.AspNetCore.Mvc;
using RamStockAlerts.Feeds;
using RamStockAlerts.Services;
using RamStockAlerts.Models;
using RamStockAlerts.Engine;

namespace RamStockAlerts.Controllers;

[ApiController]
[Route("admin")]
public class AdminController : ControllerBase
{
    private readonly OrderFlowMetrics _orderFlowMetrics;
    private readonly ILogger<AdminController> _logger;

    public AdminController(
        OrderFlowMetrics orderFlowMetrics,
        ILogger<AdminController> logger)
    {
        _orderFlowMetrics = orderFlowMetrics;
        _logger = logger;
    }

    /// <summary>
    /// Test IBKR Level II data feed - get current order book snapshot for a symbol
    /// </summary>
    [HttpGet("ibkr/orderbook/{symbol}")]
    [ProducesResponseType(typeof(OrderBookSnapshot), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public IActionResult GetIbkrOrderBook(string symbol)
    {
        var orderBook = _orderFlowMetrics.GetOrderBookSnapshot(symbol.ToUpper());
        
        if (orderBook == null)
        {
            return NotFound(new { error = $"No order book data available for {symbol}. Ensure IBKR is connected and symbol is subscribed." });
        }

        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var ageSeconds = (now - orderBook.LastUpdateMs) / 1000.0;

        return Ok(new OrderBookSnapshot
        {
            Symbol = orderBook.Symbol,
            BestBid = orderBook.BestBid,
            BestAsk = orderBook.BestAsk,
            SpreadCents = orderBook.Spread,
            BidSize4Level = orderBook.TotalBidSize4Level,
            AskSize4Level = orderBook.TotalAskSize4Level,
            BidLevels = orderBook.BidLevels
                .Take(10)
                .Select(x => new PriceLevel 
                { 
                    Price = x.Price, 
                    Size = x.Size,
                    AgeMs = now - x.TimestampMs
                })
                .ToList(),
            AskLevels = orderBook.AskLevels
                .Take(10)
                .Select(x => new PriceLevel 
                { 
                    Price = x.Price, 
                    Size = x.Size,
                    AgeMs = now - x.TimestampMs
                })
                .ToList(),
            RecentTradesCount = orderBook.RecentTrades.Count,
            LastUpdateAgeSeconds = ageSeconds
        });
    }

    /// <summary>
    /// Get IBKR connection status
    /// </summary>
    [HttpGet("ibkr/status")]
    [ProducesResponseType(typeof(IbkrStatus), StatusCodes.Status200OK)]
    public IActionResult GetIbkrStatus()
    {
        var subscribedSymbols = _orderFlowMetrics.GetSubscribedSymbols();
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        
        var symbolStats = subscribedSymbols.Select(symbol =>
        {
            var book = _orderFlowMetrics.GetOrderBookSnapshot(symbol);
            return new SymbolStatus
            {
                Symbol = symbol,
                HasData = book != null && book.LastUpdateMs > 0,
                LastUpdateAgeSeconds = book != null ? (now - book.LastUpdateMs) / 1000.0 : null,
                BidLevels = book?.BidLevels.Count() ?? 0,
                AskLevels = book?.AskLevels.Count() ?? 0,
                RecentTrades = book?.RecentTrades.Count ?? 0
            };
        }).ToList();

        return Ok(new IbkrStatus
        {
            SubscribedSymbolCount = subscribedSymbols.Count,
            SymbolsWithData = symbolStats.Count(s => s.HasData),
            Symbols = symbolStats
        });
    }
}

public class OrderBookSnapshot
{
    public string Symbol { get; set; } = string.Empty;
    public decimal BestBid { get; set; }
    public decimal BestAsk { get; set; }
    public decimal SpreadCents { get; set; }
    public decimal BidSize4Level { get; set; }
    public decimal AskSize4Level { get; set; }
    public List<PriceLevel> BidLevels { get; set; } = new();
    public List<PriceLevel> AskLevels { get; set; } = new();
    public int RecentTradesCount { get; set; }
    public double LastUpdateAgeSeconds { get; set; }
}

public class PriceLevel
{
    public decimal Price { get; set; }
    public decimal Size { get; set; }
    public long AgeMs { get; set; }
}

public class IbkrStatus
{
    public int SubscribedSymbolCount { get; set; }
    public int SymbolsWithData { get; set; }
    public List<SymbolStatus> Symbols { get; set; } = new();
}

public class SymbolStatus
{
    public string Symbol { get; set; } = string.Empty;
    public bool HasData { get; set; }
    public double? LastUpdateAgeSeconds { get; set; }
    public int BidLevels { get; set; }
    public int AskLevels { get; set; }
    public int RecentTrades { get; set; }
}
