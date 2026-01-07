using System.Net;
using System.Text.Json;
using Polly;
using Polly.Retry;
using RamStockAlerts.Engine;
using RamStockAlerts.Models;
using RamStockAlerts.Models.Polygon;
using RamStockAlerts.Services;

namespace RamStockAlerts.Feeds;

/// <summary>
/// Background service that polls Polygon.io REST API for market data
/// and generates trade signals when liquidity conditions are met.
/// </summary>
public class PolygonRestClient : BackgroundService
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly SignalValidator _validator;
    private readonly TradeBlueprint _blueprint;
    private readonly IServiceProvider _serviceProvider;
    private readonly IConfiguration _configuration;
    private readonly ILogger<PolygonRestClient> _logger;
    private readonly UniverseBuilder _universeBuilder;
    private readonly CircuitBreakerService _circuitBreaker;
    private readonly ApiQuotaTracker _quotaTracker;
    private readonly AsyncRetryPolicy<HttpResponseMessage> _retryPolicy;

    private readonly Dictionary<string, decimal> _lastSpreads = new();

    private readonly string _apiKey;
    private IReadOnlyCollection<string> _tickers;
    private readonly TimeSpan _pollInterval = TimeSpan.FromSeconds(5);

    public PolygonRestClient(
        IHttpClientFactory httpClientFactory,
        SignalValidator validator,
        TradeBlueprint blueprint,
        IServiceProvider serviceProvider,
        IConfiguration configuration,
        ILogger<PolygonRestClient> logger,
        UniverseBuilder universeBuilder,
        CircuitBreakerService circuitBreaker,
        ApiQuotaTracker quotaTracker)
    {
        _httpClientFactory = httpClientFactory;
        _validator = validator;
        _blueprint = blueprint;
        _serviceProvider = serviceProvider;
        _configuration = configuration;
        _logger = logger;
        _universeBuilder = universeBuilder;
        _circuitBreaker = circuitBreaker;
        _quotaTracker = quotaTracker;

        _apiKey = configuration["Polygon:ApiKey"] ?? "";
        _tickers = configuration.GetSection("Polygon:Tickers").Get<List<string>>() 
            ?? new List<string> { "AAPL", "TSLA", "NVDA" };

        // Log the API key status for debugging
        _logger.LogInformation("Polygon API Key configured: {HasKey}, Length: {Length}", 
            !string.IsNullOrEmpty(_apiKey), _apiKey?.Length ?? 0);

        // Retry policy with exponential backoff
        _retryPolicy = Policy
            .HandleResult<HttpResponseMessage>(r => !r.IsSuccessStatusCode && r.StatusCode != HttpStatusCode.NotFound)
            .WaitAndRetryAsync(
                3,
                retryAttempt => TimeSpan.FromSeconds(Math.Pow(2, retryAttempt)),
                (response, timespan, retryCount, context) =>
                {
                    _logger.LogWarning(
                        "Polygon API call failed with {StatusCode}, retry {RetryCount} in {Delay}s",
                        response.Result?.StatusCode, retryCount, timespan.TotalSeconds);
                    return Task.CompletedTask;
                });
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (string.IsNullOrEmpty(_apiKey))
        {
            _logger.LogWarning("Polygon API key not configured. PolygonRestClient is disabled.");
            return;
        }

        _tickers = await _universeBuilder.GetActiveUniverseAsync(stoppingToken);

        _logger.LogInformation(
            "PolygonRestClient started. Polling {TickerCount} tickers every {Interval}s",
            _tickers.Count, _pollInterval.TotalSeconds);

        // Wait a bit before starting to let the app fully initialize
        await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (_universeBuilder.ShouldRebuildNow(DateTime.UtcNow))
                {
                    _tickers = await _universeBuilder.BuildUniverseAsync(stoppingToken);
                }

                await PollAllTickersAsync(stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(ex, "Error in polling cycle");
            }

            await Task.Delay(_pollInterval, stoppingToken);
        }
    }

    private async Task PollAllTickersAsync(CancellationToken stoppingToken)
    {
        foreach (var ticker in _tickers)
        {
            if (stoppingToken.IsCancellationRequested) break;

            if (_circuitBreaker.IsSuspended())
            {
                _logger.LogWarning("Circuit breaker active; skipping ticker processing.");
                await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
                return;
            }

            try
            {
                await ProcessTickerAsync(ticker, stoppingToken);
                
                // Small delay between tickers to avoid rate limits
                await Task.Delay(TimeSpan.FromMilliseconds(500), stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(ex, "Error processing {Ticker}", ticker);
            }
        }
    }

    private async Task ProcessTickerAsync(string ticker, CancellationToken stoppingToken)
    {
        // Fetch previous day aggregate (has VWAP) - FREE TIER
        var aggregate = await FetchPreviousDayAggregateAsync(ticker, stoppingToken);
        if (aggregate == null)
        {
            _logger.LogDebug("No aggregate data for {Ticker}", ticker);
            return;
        }

        // Build market data models (estimate bid/ask from daily data)
        var (orderBook, tapeData, vwapData, spread) = BuildMarketDataFromAggregate(ticker, aggregate);
        _lastSpreads.TryGetValue(ticker, out var previousSpread);

        // Calculate liquidity score
        var score = _validator.CalculateLiquidityScore(orderBook, tapeData, vwapData, spread);

        _logger.LogInformation(
            "{Ticker}: Price={Price:F2}, VWAP={Vwap:F2}, Spread={Spread:F4}, Score={Score:F1}",
            ticker, aggregate.Close, aggregate.Vwap, spread, score);

        // Check if this is a valid setup
        if (!_validator.IsValidSetup(orderBook, tapeData, vwapData, spread, DateTime.UtcNow, previousSpread))
        {
            _logger.LogDebug("{Ticker} score {Score:F1} below threshold", ticker, score);
            _lastSpreads[ticker] = spread;
            return;
        }

        if (_circuitBreaker.ShouldThrottle(spread, tapeData.PrintsPerSecond, DateTime.UtcNow))
        {
            _lastSpreads[ticker] = spread;
            return;
        }

        _logger.LogInformation(
            "🎯 Valid setup detected for {Ticker}! Score={Score:F1}",
            ticker, score);

        // Generate trade signal
        try
        {
            var signal = _blueprint.Generate(ticker, aggregate.Close, aggregate.Vwap, spread, score);

            // Save using scoped service
            using var scope = _serviceProvider.CreateScope();
            var signalService = scope.ServiceProvider.GetRequiredService<SignalService>();
            await signalService.SaveSignalAsync(signal);
        }
        catch (InvalidOperationException ex)
        {
            _logger.LogDebug("Signal rejected: {Message}", ex.Message);
        }
        finally
        {
            _lastSpreads[ticker] = spread;
        }
    }

    private async Task<PolygonAggregate?> FetchPreviousDayAggregateAsync(string ticker, CancellationToken stoppingToken)
    {
        // Check quota before making request
        if (!_quotaTracker.CanMakeRequest())
        {
            var delay = _quotaTracker.GetRequiredDelay();
            if (delay > TimeSpan.Zero)
            {
                _logger.LogWarning("Rate limit approaching, delaying request by {Delay}ms", delay.TotalMilliseconds);
                await Task.Delay(delay, stoppingToken);
            }
            else
            {
                _logger.LogWarning("Daily quota exceeded, skipping request for {Ticker}", ticker);
                return null;
            }
        }

        var url = $"https://api.polygon.io/v2/aggs/ticker/{ticker}/prev?apiKey={_apiKey}";

        using var httpClient = _httpClientFactory.CreateClient("Polygon");
        var response = await _retryPolicy.ExecuteAsync(async () =>
            await httpClient.GetAsync(url, stoppingToken));

        // Record the request for quota tracking
        _quotaTracker.RecordRequest();

        if (!response.IsSuccessStatusCode)
        {
            _logger.LogWarning("Failed to fetch aggregate for {Ticker}: {Status}", ticker, response.StatusCode);
            return null;
        }

        var json = await response.Content.ReadAsStringAsync(stoppingToken);
        var data = JsonSerializer.Deserialize<PolygonAggregateResponse>(json);

        return data?.Results?.FirstOrDefault();
    }

    /// <summary>
    /// Build market data from aggregate only (free tier compatible).
    /// Estimates bid/ask spread from daily high-low range.
    /// </summary>
    private (OrderBook, TapeData, VwapData, decimal) BuildMarketDataFromAggregate(
        string ticker, PolygonAggregate aggregate)
    {
        // Estimate spread from daily range as percentage of price
        // Typical liquid stocks have ~0.01-0.03% spread, we use high-low as proxy
        var dailyRange = aggregate.High - aggregate.Low;
        var estimatedSpread = aggregate.Close > 0 
            ? Math.Min(dailyRange / aggregate.Close * 0.1m, 0.05m) // Cap at 5%
            : 0.02m;
        
        // For highly liquid stocks, use a tighter spread estimate
        if (aggregate.Volume > 10_000_000) estimatedSpread = Math.Min(estimatedSpread, 0.01m);

        // Build OrderBook with estimated values
        // High volume + close near high = bullish (bid > ask)
        var closePosition = aggregate.High != aggregate.Low 
            ? (aggregate.Close - aggregate.Low) / (aggregate.High - aggregate.Low)
            : 0.5m;
        var estimatedBidAskRatio = 1m + (closePosition - 0.5m) * 4m; // Range: ~-1 to ~3

        var orderBook = new OrderBook
        {
            BidAskRatio = Math.Max(0.1m, estimatedBidAskRatio),
            TotalBidSize = aggregate.Volume * closePosition,
            TotalAskSize = aggregate.Volume * (1 - closePosition),
            Spread = estimatedSpread,
            Timestamp = DateTime.UtcNow
        };

        // Build TapeData from transaction count
        // Trading day is 6.5 hours = 23400 seconds
        var printsPerSecond = aggregate.NumberOfTransactions > 0
            ? aggregate.NumberOfTransactions / 23400m
            : 5m; // Default to moderate activity

        var tapeData = new TapeData
        {
            PrintsPerSecond = printsPerSecond,
            LastPrintSize = aggregate.Volume > 0 && aggregate.NumberOfTransactions > 0
                ? aggregate.Volume / aggregate.NumberOfTransactions
                : 100m,
            Timestamp = DateTime.UtcNow
        };

        // Build VwapData - close above VWAP is bullish
        var hasReclaim = aggregate.Close >= aggregate.Vwap;
        var vwapData = new VwapData
        {
            CurrentPrice = aggregate.Close,
            VwapPrice = aggregate.Vwap,
            HasReclaim = hasReclaim
        };

        return (orderBook, tapeData, vwapData, estimatedSpread);
    }
}
