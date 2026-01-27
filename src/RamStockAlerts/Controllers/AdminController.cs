using Microsoft.AspNetCore.Mvc;
using RamStockAlerts.Controllers.Api.Admin;
using RamStockAlerts.Feeds;
using RamStockAlerts.Services;
using RamStockAlerts.Models;
using RamStockAlerts.Models.Notifications;
using RamStockAlerts.Engine;
using RamStockAlerts.Execution.Contracts;

namespace RamStockAlerts.Controllers;

[ApiController]
[Route("admin")]
public class AdminController : ControllerBase
{
    private readonly OrderFlowMetrics _orderFlowMetrics;
    private readonly DiscordNotificationSettingsStore _discordSettingsStore;
    private readonly DiscordDeliveryStatusStore _discordStatusStore;
    private readonly DiscordNotificationService _discordNotificationService;
    private readonly ILogger<AdminController> _logger;
    private readonly ExecutionOptions _executionOptions;

    public AdminController(
        OrderFlowMetrics orderFlowMetrics,
        DiscordNotificationSettingsStore discordSettingsStore,
        DiscordDeliveryStatusStore discordStatusStore,
        DiscordNotificationService discordNotificationService,
        ExecutionOptions executionOptions,
        ILogger<AdminController> logger)
    {
        _orderFlowMetrics = orderFlowMetrics;
        _discordSettingsStore = discordSettingsStore;
        _discordStatusStore = discordStatusStore;
        _discordNotificationService = discordNotificationService;
        _executionOptions = executionOptions;
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

    [HttpPost("execution/monitor-only")]
    public IActionResult SetMonitorOnly([FromQuery] bool enabled)
    {
        _executionOptions.MonitorOnly = enabled;
        _logger.LogWarning(
            "[Admin] Monitor-only mode {State} by operator",
            enabled ? "ENABLED" : "DISABLED");
        return Ok(new { monitorOnly = enabled });
    }

    [HttpGet("notifications/discord")]
    [ProducesResponseType(typeof(DiscordNotificationStatusResponse), StatusCodes.Status200OK)]
    public IActionResult GetDiscordNotificationStatus()
    {
        var settings = _discordSettingsStore.GetSettings();
        var status = _discordStatusStore.GetStatusForWebhook(settings.WebhookUrl);

        return Ok(new DiscordNotificationStatusResponse
        {
            Settings = ToSettingsDto(settings),
            Status = ToStatusDto(status)
        });
    }

    [HttpPut("notifications/discord")]
    [ProducesResponseType(typeof(DiscordNotificationStatusResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public IActionResult UpdateDiscordNotificationSettings([FromBody] DiscordNotificationSettingsRequest request)
    {
        if (request is null)
        {
            return BadRequest(new { error = "Request body is required." });
        }

        if (!TryValidateWebhookUrl(request.WebhookUrl, out var normalizedWebhook))
        {
            return BadRequest(new { error = "WebhookUrl must be a valid absolute http(s) URL." });
        }

        var existing = _discordSettingsStore.GetSettings();
        var updated = new DiscordNotificationSettings
        {
            Enabled = request.Enabled,
            WebhookUrl = normalizedWebhook,
            ChannelTag = string.IsNullOrWhiteSpace(request.ChannelTag) ? null : request.ChannelTag.Trim(),
            IncludeModeTag = existing.IncludeModeTag,
            CompactAlertFields = existing.CompactAlertFields
        };

        _discordSettingsStore.UpdateSettings(updated);
        var status = _discordStatusStore.GetStatusForWebhook(updated.WebhookUrl);

        return Ok(new DiscordNotificationStatusResponse
        {
            Settings = ToSettingsDto(updated),
            Status = ToStatusDto(status)
        });
    }

    [HttpPost("notifications/discord/test")]
    [ProducesResponseType(typeof(DiscordTestResponse), StatusCodes.Status200OK)]
    public async Task<IActionResult> SendDiscordTest([FromBody] DiscordTestRequest? request)
    {
        var result = await _discordNotificationService.SendTestAsync(request?.Message);

        return Ok(new DiscordTestResponse
        {
            Success = result.Success,
            Status = ToStatusDto(result.Status)
        });
    }

    private static DiscordNotificationSettingsDto ToSettingsDto(DiscordNotificationSettings settings)
    {
        return new DiscordNotificationSettingsDto
        {
            Enabled = settings.Enabled,
            WebhookUrlMasked = MaskWebhookUrl(settings.WebhookUrl),
            ChannelTag = settings.ChannelTag
        };
    }

    private static DiscordDeliveryStatusDto? ToStatusDto(DiscordDeliveryStatus? status)
    {
        if (status == null)
        {
            return null;
        }

        return new DiscordDeliveryStatusDto
        {
            LastAttemptAt = status.LastAttemptAt,
            LastSuccessAt = status.LastSuccessAt,
            LastFailureAt = status.LastFailureAt,
            LastStatusCode = status.LastStatusCode,
            LastError = status.LastError
        };
    }

    private static bool TryValidateWebhookUrl(string webhookUrl, out string? normalizedWebhook)
    {
        normalizedWebhook = null;
        if (string.IsNullOrWhiteSpace(webhookUrl))
        {
            return false;
        }

        var trimmed = webhookUrl.Trim();
        if (!Uri.TryCreate(trimmed, UriKind.Absolute, out var uri))
        {
            return false;
        }

        if (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp)
        {
            return false;
        }

        normalizedWebhook = trimmed;
        return true;
    }

    private static string? MaskWebhookUrl(string? webhookUrl)
    {
        if (string.IsNullOrWhiteSpace(webhookUrl))
        {
            return null;
        }

        var trimmed = webhookUrl.Trim();
        if (trimmed.Length <= 12)
        {
            return "****";
        }

        return $"{trimmed[..8]}****{trimmed[^4..]}";
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
