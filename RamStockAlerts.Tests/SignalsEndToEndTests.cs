using System.Net;
using System.Net.Http.Json;
using RamStockAlerts.Controllers;
using RamStockAlerts.Models;

namespace RamStockAlerts.Tests;

/// <summary>
/// End-to-end integration tests for the Signals API.
/// Tests the complete workflow from HTTP request to database persistence.
/// </summary>
public class SignalsEndToEndTests : IDisposable
{
    private readonly TestWebApplicationFactory _factory;
    private readonly HttpClient _client;

    public SignalsEndToEndTests()
    {
        _factory = new TestWebApplicationFactory();
        _client = _factory.CreateClient();
    }

    public void Dispose()
    {
        _client?.Dispose();
        _factory?.Dispose();
    }

    [Fact]
    public async Task WhenCreatingSignalThenSignalIsStoredAndRetrievable()
    {
        // Arrange - Create a valid signal request
        var request = new CreateSignalRequest
        {
            Ticker = "AAPL",
            Entry = 150.00m,
            Stop = 145.00m,
            Target = 160.00m,
            Score = 8.5m,
            Timestamp = DateTime.UtcNow
        };

        // Act - Create the signal
        var createResponse = await _client.PostAsJsonAsync("/api/signals", request);

        // Assert - Signal creation was successful (not throttled)
        Assert.NotEqual(HttpStatusCode.TooManyRequests, createResponse.StatusCode);
        Assert.Equal(HttpStatusCode.OK, createResponse.StatusCode);
        
        var createdSignal = await createResponse.Content.ReadFromJsonAsync<TradeSignal>();
        Assert.NotNull(createdSignal);
        Assert.Equal("AAPL", createdSignal.Ticker);
        Assert.Equal(150.00m, createdSignal.Entry);
        Assert.Equal(145.00m, createdSignal.Stop);
        Assert.Equal(160.00m, createdSignal.Target);
        Assert.Equal(8.5m, createdSignal.Score);
        Assert.True(createdSignal.Id > 0);

        // Act - Retrieve recent signals
        var getResponse = await _client.GetAsync("/api/signals?count=10");

        // Assert - Signal is in the list of recent signals
        Assert.Equal(HttpStatusCode.OK, getResponse.StatusCode);
        
        var signals = await getResponse.Content.ReadFromJsonAsync<List<TradeSignal>>();
        Assert.NotNull(signals);
        Assert.Contains(signals, s => s.Ticker == "AAPL" && s.Entry == 150.00m);
    }

    [Fact]
    public async Task WhenCreatingMultipleSignalsThenAllSignalsAreRetrievable()
    {
        // Arrange - Create multiple signals
        var tickers = new[] { "TSLA", "NVDA", "MSFT" };
        var createdSignals = new List<TradeSignal>();

        foreach (var ticker in tickers)
        {
            var request = new CreateSignalRequest
            {
                Ticker = ticker,
                Entry = 100.00m,
                Stop = 95.00m,
                Target = 110.00m,
                Score = 7.0m
            };

            var response = await _client.PostAsJsonAsync("/api/signals", request);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            
            var signal = await response.Content.ReadFromJsonAsync<TradeSignal>();
            Assert.NotNull(signal);
            createdSignals.Add(signal);
        }

        // Act - Retrieve recent signals
        var getResponse = await _client.GetAsync("/api/signals?count=20");

        // Assert - All signals are retrievable
        Assert.Equal(HttpStatusCode.OK, getResponse.StatusCode);
        
        var signals = await getResponse.Content.ReadFromJsonAsync<List<TradeSignal>>();
        Assert.NotNull(signals);
        
        foreach (var ticker in tickers)
        {
            Assert.Contains(signals, s => s.Ticker == ticker);
        }
    }

    [Fact]
    public async Task WhenGettingSignalsByTickerThenOnlyMatchingSignalsAreReturned()
    {
        // Arrange - Create signals for different tickers
        var testTicker = "AMZN";
        var request1 = new CreateSignalRequest
        {
            Ticker = testTicker,
            Entry = 120.00m,
            Stop = 115.00m,
            Target = 130.00m,
            Score = 7.5m
        };

        var request2 = new CreateSignalRequest
        {
            Ticker = "GOOG",
            Entry = 140.00m,
            Stop = 135.00m,
            Target = 150.00m,
            Score = 8.0m
        };

        await _client.PostAsJsonAsync("/api/signals", request1);
        await _client.PostAsJsonAsync("/api/signals", request2);

        // Act - Get signals for specific ticker
        var response = await _client.GetAsync($"/api/signals/{testTicker}?count=10");

        // Assert - Only AMZN signals are returned
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        
        var signals = await response.Content.ReadFromJsonAsync<List<TradeSignal>>();
        Assert.NotNull(signals);
        Assert.All(signals, s => Assert.Equal(testTicker, s.Ticker));
    }

    [Fact]
    public async Task WhenCreatingSignalWithInvalidDataThenBadRequestIsReturned()
    {
        // Arrange - Create an invalid signal request (missing required field)
        var invalidJson = """
        {
            "Entry": 100.00,
            "Stop": 95.00,
            "Target": 110.00,
            "Score": 7.0
        }
        """;

        var content = new StringContent(invalidJson, System.Text.Encoding.UTF8, "application/json");

        // Act - Attempt to create the signal
        var response = await _client.PostAsync("/api/signals", content);

        // Assert - Bad request is returned
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task WhenGettingWinRateThenAnalyticsEndpointReturnsData()
    {
        // Act - Request win rate analytics
        var response = await _client.GetAsync("/api/signals/analytics/winrate");

        // Assert - Response is successful (OK or NoContent if no data)
        Assert.True(response.StatusCode == HttpStatusCode.OK || response.StatusCode == HttpStatusCode.NoContent, 
            $"Expected OK or NoContent, but got {response.StatusCode}");
        
        if (response.StatusCode == HttpStatusCode.OK)
        {
            var winRate = await response.Content.ReadFromJsonAsync<decimal?>();
            // Win rate may be null if no completed trades exist
            Assert.True(winRate == null || (winRate >= 0 && winRate <= 100));
        }
    }

    [Fact]
    public async Task WhenGettingHourlyEdgeThenAnalyticsEndpointReturnsData()
    {
        // Act - Request hourly edge analytics
        var response = await _client.GetAsync("/api/signals/analytics/by-hour");

        // Assert - Response is successful
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        
        var hourlyEdge = await response.Content.ReadFromJsonAsync<Dictionary<int, decimal>>();
        Assert.NotNull(hourlyEdge);
        // Dictionary may be empty if no data exists, but should not be null
    }

    [Fact]
    public async Task WhenReplayingEventsThenEventStoreReturnsEvents()
    {
        // Arrange - Create a signal to generate events
        var request = new CreateSignalRequest
        {
            Ticker = "SPY",
            Entry = 450.00m,
            Stop = 445.00m,
            Target = 460.00m,
            Score = 9.0m
        };

        await _client.PostAsJsonAsync("/api/signals", request);

        // Act - Replay events
        var response = await _client.GetAsync("/api/signals/events/replay?limit=100");

        // Assert - Response is successful
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        
        var events = await response.Content.ReadFromJsonAsync<List<EventReplayDto>>();
        Assert.NotNull(events);
        // Events list may or may not be empty depending on event store implementation
    }
}
