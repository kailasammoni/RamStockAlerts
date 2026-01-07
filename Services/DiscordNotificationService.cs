using System.Net;
using System.Text;
using System.Text.Json;
using Polly;
using Polly.Retry;
using RamStockAlerts.Models;

namespace RamStockAlerts.Services;

public class DiscordNotificationService
{
    private readonly HttpClient _httpClient;
    private readonly string _webhookUrl;
    private readonly ILogger<DiscordNotificationService> _logger;
    private readonly AsyncRetryPolicy<HttpResponseMessage> _retryPolicy;

    public DiscordNotificationService(
        HttpClient httpClient,
        IConfiguration configuration,
        ILogger<DiscordNotificationService> logger)
    {
        _httpClient = httpClient;
        _webhookUrl = configuration["Discord:WebhookUrl"] 
            ?? throw new InvalidOperationException("Discord:WebhookUrl not configured");
        _logger = logger;

        // Configure Polly retry policy: 3 retries with exponential backoff
        // Special handling for 429 (rate limit)
        _retryPolicy = Policy
            .HandleResult<HttpResponseMessage>(r => !r.IsSuccessStatusCode)
            .WaitAndRetryAsync(
                3,
                (retryAttempt, response, context) =>
                {
                    // If rate limited (429), wait 5 seconds
                    if (response.Result?.StatusCode == HttpStatusCode.TooManyRequests)
                    {
                        return TimeSpan.FromSeconds(5);
                    }
                    // Otherwise exponential backoff
                    return TimeSpan.FromSeconds(Math.Pow(2, retryAttempt));
                },
                (response, timespan, retryCount, context) =>
                {
                    _logger.LogWarning(
                        "Discord webhook failed with {StatusCode}, retry {RetryCount} in {Delay}s",
                        response.Result?.StatusCode,
                        retryCount,
                        timespan.TotalSeconds);
                    return Task.CompletedTask;
                });
    }

    public async Task SendSignalAsync(TradeSignal signal)
    {
        var embed = BuildEmbed(signal);
        var payload = new { embeds = new[] { embed } };
        var json = JsonSerializer.Serialize(payload);
        var content = new StringContent(json, Encoding.UTF8, "application/json");

        var response = await _retryPolicy.ExecuteAsync(async () =>
            await _httpClient.PostAsync(_webhookUrl, content));

        if (!response.IsSuccessStatusCode)
        {
            var errorContent = await response.Content.ReadAsStringAsync();
            throw new HttpRequestException(
                $"Discord webhook failed after retries: {response.StatusCode} - {errorContent}");
        }

        _logger.LogInformation(
            "Discord notification sent for {Ticker} (Score: {Score})",
            signal.Ticker, signal.Score);
    }

    private object BuildEmbed(TradeSignal signal)
    {
        // Green color for high scores (>= 8), yellow for marginal
        var color = signal.Score >= 8m ? 3066993 : 15844367;

        return new
        {
            title = "🚨 Liquidity Setup Detected",
            color,
            fields = new[]
            {
                new { name = "Ticker", value = signal.Ticker, inline = true },
                new { name = "Entry", value = $"${signal.Entry:F2}", inline = true },
                new { name = "Stop", value = $"${signal.Stop:F2}", inline = true },
                new { name = "Target", value = $"${signal.Target:F2}", inline = true },
                new { name = "Score", value = $"{signal.Score:F1}/10", inline = true },
                new { name = "Risk/Reward", value = GetRiskRewardRatio(signal), inline = true }
            },
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
