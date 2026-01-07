using System.Net;
using System.Text;
using System.Text.Json;
using Polly;
using Polly.Retry;
using RamStockAlerts.Models;

namespace RamStockAlerts.Services;

/// <summary>
/// Discord webhook alert channel with retry logic.
/// </summary>
public class DiscordAlertChannel : IAlertChannel
{
    private readonly HttpClient _httpClient;
    private readonly string _webhookUrl;
    private readonly ILogger<DiscordAlertChannel> _logger;
    private readonly AsyncRetryPolicy<HttpResponseMessage> _retryPolicy;

    public string ChannelName => "Discord";

    public DiscordAlertChannel(
        HttpClient httpClient,
        IConfiguration configuration,
        ILogger<DiscordAlertChannel> logger)
    {
        _httpClient = httpClient;
        _webhookUrl = configuration["Discord:WebhookUrl"] ?? "";
        _logger = logger;

        _retryPolicy = Policy
            .HandleResult<HttpResponseMessage>(r => !r.IsSuccessStatusCode)
            .WaitAndRetryAsync(
                3,
                (retryAttempt, response, context) =>
                {
                    if (response.Result?.StatusCode == HttpStatusCode.TooManyRequests)
                        return TimeSpan.FromSeconds(5);
                    return TimeSpan.FromSeconds(Math.Pow(2, retryAttempt));
                },
                (response, timespan, retryCount, context) =>
                {
                    _logger.LogWarning(
                        "Discord webhook failed with {StatusCode}, retry {RetryCount} in {Delay}s",
                        response.Result?.StatusCode, retryCount, timespan.TotalSeconds);
                    return Task.CompletedTask;
                });
    }

    public async Task<bool> SendAsync(TradeSignal signal, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(_webhookUrl))
        {
            _logger.LogWarning("Discord webhook URL not configured");
            return false;
        }

        try
        {
            var embed = BuildEmbed(signal);
            var payload = new { embeds = new[] { embed } };
            var json = JsonSerializer.Serialize(payload);
            var content = new StringContent(json, Encoding.UTF8, "application/json");

            var response = await _retryPolicy.ExecuteAsync(async () =>
                await _httpClient.PostAsync(_webhookUrl, content, cancellationToken));

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogError("Discord webhook failed: {StatusCode}", response.StatusCode);
                return false;
            }

            _logger.LogInformation("Discord alert sent for {Ticker}", signal.Ticker);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Discord alert failed for {Ticker}", signal.Ticker);
            return false;
        }
    }

    private object BuildEmbed(TradeSignal signal)
    {
        var color = signal.Score >= 8m ? 3066993 : 15844367;
        
        var fields = new List<object>
        {
            new { name = "Ticker", value = signal.Ticker, inline = true },
            new { name = "Entry", value = $"${signal.Entry:F2}", inline = true },
            new { name = "Stop", value = $"${signal.Stop:F2}", inline = true },
            new { name = "Target", value = $"${signal.Target:F2}", inline = true },
            new { name = "Score", value = $"{signal.Score:F1}/10", inline = true },
            new { name = "Risk/Reward", value = GetRiskRewardRatio(signal), inline = true }
        };
        
        // Add order information if available
        if (signal.OrderId != null)
        {
            fields.Add(new { name = "🤖 Auto-Trade", value = $"✅ Order Placed", inline = true });
            fields.Add(new { name = "Position", value = $"{signal.PositionSize} shares", inline = true });
            fields.Add(new { name = "Order ID", value = signal.OrderId.Substring(0, Math.Min(8, signal.OrderId.Length)) + "...", inline = true });
            color = 3447003; // Blue color for auto-traded signals
        }
        else if (signal.AutoTradingAttempted)
        {
            fields.Add(new { name = "🤖 Auto-Trade", value = $"⏭️ Skipped", inline = true });
            if (!string.IsNullOrEmpty(signal.AutoTradingSkipReason))
            {
                fields.Add(new { name = "Reason", value = signal.AutoTradingSkipReason, inline = false });
            }
        }

        return new
        {
            title = signal.OrderId != null ? "🚨 Liquidity Setup + AUTO-TRADE" : "🚨 Liquidity Setup Detected",
            color,
            fields = fields.ToArray(),
            timestamp = signal.Timestamp.ToString("o"),
            footer = new { text = "RamStockAlerts • Liquidity Engine" },
            thumbnail = new { url = "https://cdn-icons-png.flaticon.com/512/2920/2920299.png" }
        };
    }

    private static string GetRiskRewardRatio(TradeSignal signal)
    {
        var risk = signal.Entry - signal.Stop;
        var reward = signal.Target - signal.Entry;
        if (risk == 0) return "N/A";
        var ratio = reward / risk;
        return $"1:{ratio:F1}";
    }
}
