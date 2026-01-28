using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using RamStockAlerts.Services;
using Xunit;

namespace RamStockAlerts.Tests;

public class DiscordNotificationServiceTests
{
    [Fact]
    public async Task SendAlertAsync_FullFields_IncludesModeTimestampAndMetrics()
    {
        var service = CreateService(compact: false, out var handler);
        var details = BuildDetails();

        await service.SendAlertAsync(
            "AAPL",
            "Liquidity Setup",
            DateTimeOffset.Parse("2024-06-01T12:00:00Z"),
            mode: RamStockAlerts.Models.Notifications.DiscordNotificationMode.Preview,
            intendedAction: "Long",
            details);

        var payload = Assert.Single(handler.Payloads);
        var fieldNames = ExtractFieldNames(payload);

        Assert.Contains("Mode", fieldNames);
        Assert.Contains("Timestamp", fieldNames);
        Assert.Contains("Entry", fieldNames);
        Assert.Contains("Stop", fieldNames);
        Assert.Contains("Target", fieldNames);
        Assert.Contains("Spread", fieldNames);
        Assert.Contains("BidAskRatio", fieldNames);
        Assert.Contains("TapeVelocityProxy", fieldNames);
    }

    [Fact]
    public async Task SendAlertAsync_CompactFields_DropsSecondaryMetricsAndCombinesBlueprint()
    {
        var service = CreateService(compact: true, out var handler);
        var details = BuildDetails();

        await service.SendAlertAsync(
            "AAPL",
            "Liquidity Setup",
            DateTimeOffset.Parse("2024-06-01T12:00:00Z"),
            mode: RamStockAlerts.Models.Notifications.DiscordNotificationMode.Preview,
            intendedAction: "Long",
            details);

        var payload = Assert.Single(handler.Payloads);
        var fields = ExtractFields(payload);
        var fieldNames = fields.Select(field => field.Name).ToList();

        Assert.DoesNotContain("Mode", fieldNames);
        Assert.DoesNotContain("Timestamp", fieldNames);
        Assert.DoesNotContain("Entry", fieldNames);
        Assert.DoesNotContain("Stop", fieldNames);
        Assert.DoesNotContain("Target", fieldNames);
        Assert.DoesNotContain("Spread", fieldNames);
        Assert.DoesNotContain("BidAskRatio", fieldNames);
        Assert.DoesNotContain("TapeVelocityProxy", fieldNames);

        var blueprint = fields.Single(field => field.Name == "Blueprint");
        Assert.Equal("Entry 101.00 | Stop 100.00 | Target 104.00", blueprint.Value);
    }

    private static IReadOnlyDictionary<string, string> BuildDetails()
    {
        return new Dictionary<string, string>
        {
            ["Entry"] = "101.00",
            ["Stop"] = "100.00",
            ["Target"] = "104.00",
            ["Spread"] = "0.0400",
            ["BidAskRatio"] = "1.25",
            ["TapeVelocityProxy"] = "2.50"
        };
    }

    private static DiscordNotificationService CreateService(bool compact, out CapturingHandler handler)
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string>
            {
                ["Discord:Enabled"] = "true",
                ["Discord:WebhookUrl"] = "https://example.com/webhook",
                ["Discord:CompactAlertFields"] = compact.ToString()
            })
            .Build();

        var settingsStore = new DiscordNotificationSettingsStore(config);
        var statusStore = new DiscordDeliveryStatusStore();
        handler = new CapturingHandler();
        var client = new HttpClient(handler);
        var httpClientFactory = new TestHttpClientFactory(client);

        return new DiscordNotificationService(
            settingsStore,
            statusStore,
            httpClientFactory,
            NullLogger<DiscordNotificationService>.Instance);
    }

    private static List<string> ExtractFieldNames(string payload)
    {
        return ExtractFields(payload).Select(field => field.Name).ToList();
    }

    private static List<(string Name, string Value)> ExtractFields(string payload)
    {
        using var document = JsonDocument.Parse(payload);
        var fields = document.RootElement.GetProperty("embeds")[0].GetProperty("fields");
        return fields.EnumerateArray()
            .Select(field => (
                field.GetProperty("name").GetString() ?? string.Empty,
                field.GetProperty("value").GetString() ?? string.Empty))
            .ToList();
    }

    private sealed class TestHttpClientFactory : IHttpClientFactory
    {
        private readonly HttpClient _client;

        public TestHttpClientFactory(HttpClient client)
        {
            _client = client;
        }

        public HttpClient CreateClient(string name)
        {
            return _client;
        }
    }

    private sealed class CapturingHandler : HttpMessageHandler
    {
        public List<string> Payloads { get; } = new();

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.Content != null)
            {
                Payloads.Add(await request.Content.ReadAsStringAsync(cancellationToken));
            }

            return new HttpResponseMessage(HttpStatusCode.OK);
        }
    }
}
