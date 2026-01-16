using System.Net;
using System.Text.Json;
using Polly;
using Polly.Retry;
using RamStockAlerts.Engine;
using RamStockAlerts.Models;
using RamStockAlerts.Models.Polygon;
using RamStockAlerts.Services;
using RamStockAlerts.Universe;

namespace RamStockAlerts.Feeds;

/// <summary>
/// Background service that polls Polygon.io REST API for market data.
/// 
/// ⚠️ DEVELOPMENT/TESTING ONLY ⚠️
/// This client uses daily aggregate data and estimated spreads, which cannot
/// detect the millisecond-level liquidity imbalances required for production signals.
/// 
/// Production deployments MUST use AlpacaStreamClient for real-time order book data.
/// Signals generated from this client should be marked as low-confidence.
/// </summary>
public class PolygonRestClient : BackgroundService
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly SignalValidator _validator;
    private readonly TradeBlueprint _blueprint;
    private readonly IServiceProvider _serviceProvider;
    private readonly IConfiguration _configuration;
    private readonly ILogger<PolygonRestClient> _logger;
    private readonly UniverseService _universeService;
    private readonly CircuitBreakerService _circuitBreaker;
    private readonly ApiQuotaTracker _quotaTracker;
    private readonly AsyncRetryPolicy<HttpResponseMessage> _retryPolicy;

    private readonly Dictionary<string, decimal> _lastSpreads = new();

    private readonly string? _apiKey;
    private IReadOnlyCollection<string> _tickers;
    private readonly TimeSpan _pollInterval = TimeSpan.FromSeconds(60); // Poll once per minute to respect API quotas
    private readonly string _universeSource;

    public PolygonRestClient(
        IHttpClientFactory httpClientFactory,
        SignalValidator validator,
        TradeBlueprint blueprint,
        IServiceProvider serviceProvider,
        IConfiguration configuration,
        ILogger<PolygonRestClient> logger,
        UniverseService universeService,
        CircuitBreakerService circuitBreaker,
        ApiQuotaTracker quotaTracker)
    {
        _httpClientFactory = httpClientFactory;
        _validator = validator;
        _blueprint = blueprint;
        _serviceProvider = serviceProvider;
        _configuration = configuration;
        _logger = logger;
        _universeService = universeService;
        _circuitBreaker = circuitBreaker;
        _quotaTracker = quotaTracker;

        _apiKey = configuration["Polygon:ApiKey"] ?? "";
        _universeSource = configuration["Universe:Source"]?.Trim() ?? "Legacy";
        _tickers = configuration.GetSection("Polygon:Tickers").Get<List<string>>() 
            ?? new List<string> { "AAPL", "TSLA", "NVDA" };

        // Log the API key status for debugging
        _logger.LogInformation("Polygon API Key configured: {HasKey}, Length: {Length}", 
            !string.IsNullOrEmpty(_apiKey), _apiKey?.Length ?? 0);

        _logger.LogWarning(
            "PolygonRestClient is active. This is a FALLBACK CLIENT for development/testing. " +
            "Production signals require real-time data from AlpacaStreamClient.");

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

        _tickers = await LoadUniverseAsync(stoppingToken);

        _logger.LogInformation(
            "PolygonRestClient started. Polling {TickerCount} tickers every {Interval}s",
            _tickers.Count, _pollInterval.TotalSeconds);

        // Wait a bit before starting to let the app fully initialize
        await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (UseLegacyUniverse())
                {
                    var legacyBuilder = _serviceProvider.GetRequiredService<LegacyUniverseBuilder>();
                    if (legacyBuilder.ShouldRebuildNow(DateTime.UtcNow))
                    {
                        _tickers = await legacyBuilder.BuildUniverseAsync(stoppingToken);
                    }
                }
                else
                {
                    var refreshed = await _universeService.GetUniverseAsync(stoppingToken);
                    if (!refreshed.SequenceEqual(_tickers))
                    {
                        _tickers = refreshed;
                    }
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
            // Use Close as both lastPrice and lastAsk (Polygon aggregate doesn't have bid/ask)
            var signal = _blueprint.Generate(ticker, aggregate.Close, aggregate.Close, aggregate.Vwap, spread, score);

            // Save using scoped service
            using var scope = _serviceProvider.CreateScope();
            var signalService = scope.ServiceProvider.GetRequiredService<SignalService>();
            var savedSignal = await signalService.SaveSignalAsync(signal);
            
            if (savedSignal != null)
            {
                _logger.LogInformation("✅ Signal saved and alert sent for {Ticker}", ticker);
            }
            else
            {
                _logger.LogWarning("⚠️ Signal for {Ticker} was not saved (throttled or circuit breaker active)", ticker);
            }
        }
        catch (InvalidOperationException ex)
        {
            _logger.LogWarning("Signal rejected for {Ticker}: {Message}", ticker, ex.Message);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "❌ Failed to save signal for {Ticker}", ticker);
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
                _logger.LogWarning(
                    "[Polygon Rate Limit] Rate limit approaching for {Ticker}. Delaying {Delay}ms to respect quota.",
                    ticker, delay.TotalMilliseconds);
                await Task.Delay(delay, stoppingToken);
            }
            else
            {
                _logger.LogWarning(
                    "[Polygon Quota] Daily quota exceeded for {Ticker}. Skipping request. Check API limits at polygon.io/dashboard",
                    ticker);
                return null;
            }
        }

        var url = $"https://api.polygon.io/v2/aggs/ticker/{ticker}/prev?apiKey={_apiKey}";
        var requestTime = DateTime.UtcNow;

        using var httpClient = _httpClientFactory.CreateClient("Polygon");
        _logger.LogDebug("[Polygon Request] Fetching previous day aggregate for {Ticker}", ticker);

        HttpResponseMessage response;
        try
        {
            response = await _retryPolicy.ExecuteAsync(async () =>
                await httpClient.GetAsync(url, stoppingToken));
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(
                ex,
                "[Polygon Network] Network error fetching aggregate for {Ticker}: {Message}",
                ticker, ex.Message);
            return null;
        }

        var elapsedMs = (DateTime.UtcNow - requestTime).TotalMilliseconds;

        // Record the request for quota tracking
        _quotaTracker.RecordRequest();

        // Log rate limit headers if available
        if (response.Headers.TryGetValues("X-Ratelimit-Limit", out var limitHeaders))
        {
            var limit = limitHeaders.FirstOrDefault();
            if (response.Headers.TryGetValues("X-Ratelimit-Remaining", out var remainingHeaders))
            {
                var remaining = remainingHeaders.FirstOrDefault();
                _logger.LogInformation(
                    "[Polygon Quota Status] {Ticker}: {Remaining}/{Limit} requests remaining",
                    ticker, remaining, limit);
            }
        }

        if (!response.IsSuccessStatusCode)
        {
            var statusCode = (int)response.StatusCode;
            if (statusCode == 429)
            {
                _logger.LogError(
                    "[Polygon Rate Limit ERROR] HTTP 429 Too Many Requests for {Ticker}. Respect retry-after headers. " +
                    "Free tier is 5 requests/min. Consider upgrading: polygon.io/pricing",
                    ticker);
                // Implement exponential backoff for 429 errors
                var delay = TimeSpan.FromSeconds(Math.Min(60, 5));
                _logger.LogWarning("[Polygon Backoff] Waiting {Delay}s before retry", delay.TotalSeconds);
                await Task.Delay(delay, stoppingToken);
            }
            else
            {
                _logger.LogWarning(
                    "[Polygon API Error] Failed to fetch aggregate for {Ticker}: HTTP {Status} ({Elapsed}ms)",
                    ticker, statusCode, elapsedMs);
            }
            return null;
        }

        _logger.LogDebug(
            "[Polygon Success] Received aggregate for {Ticker} ({Elapsed}ms)",
            ticker, elapsedMs);

        var json = await response.Content.ReadAsStringAsync(stoppingToken);
        var data = JsonSerializer.Deserialize<PolygonAggregateResponse>(json);

        if (data?.Results?.Any() == true)
        {
            _logger.LogDebug(
                "[Polygon Data] {Ticker}: Close={Close:F2}, High={High:F2}, Low={Low:F2}, Volume={Volume}",
                ticker, data.Results.First().Close, data.Results.First().High, data.Results.First().Low, data.Results.First().Volume);
        }

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

    private bool UseLegacyUniverse()
    {
        return string.Equals(_universeSource, "Legacy", StringComparison.OrdinalIgnoreCase);
    }

    private async Task<IReadOnlyCollection<string>> LoadUniverseAsync(CancellationToken ct)
    {
        if (UseLegacyUniverse())
        {
            var legacyBuilder = _serviceProvider.GetRequiredService<LegacyUniverseBuilder>();
            return await legacyBuilder.GetActiveUniverseAsync(ct);
        }

        return await _universeService.GetUniverseAsync(ct);
    }
}
