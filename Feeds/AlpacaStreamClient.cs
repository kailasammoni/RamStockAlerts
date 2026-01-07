using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using RamStockAlerts.Engine;
using RamStockAlerts.Models;
using RamStockAlerts.Services;

namespace RamStockAlerts.Feeds;

/// <summary>
/// WebSocket streaming client for Alpaca real-time quotes and trades.
/// Maintains rolling tape statistics and VWAP hold detection.
/// </summary>
public class AlpacaStreamClient : BackgroundService
{
    private readonly IConfiguration _configuration;
    private readonly ILogger<AlpacaStreamClient> _logger;
    private readonly IServiceProvider _serviceProvider;
    private readonly UniverseBuilder _universeBuilder;
    private readonly SignalValidator _validator;
    private readonly TradeBlueprint _blueprint;
    private readonly CircuitBreakerService _circuitBreaker;

    private readonly string _apiKey;
    private readonly string _apiSecret;
    private readonly string _streamUrl;
    private readonly int _tapeWindowSeconds;
    private readonly decimal _zScoreThreshold;
    private readonly int _vwapHoldSeconds;
    private readonly int _vwapHoldPrints;

    private ClientWebSocket? _webSocket;
    private IReadOnlyCollection<string> _subscribedSymbols = Array.Empty<string>();
    private DateTime _lastMessageTime = DateTime.UtcNow;
    private int _reconnectAttempts;
    private const int MaxReconnectAttempts = 10;
    private const int FeedLagThresholdSeconds = 5;

    // Per-symbol market state
    private readonly ConcurrentDictionary<string, SymbolState> _symbolStates = new();

    public AlpacaStreamClient(
        IConfiguration configuration,
        ILogger<AlpacaStreamClient> logger,
        IServiceProvider serviceProvider,
        UniverseBuilder universeBuilder,
        SignalValidator validator,
        TradeBlueprint blueprint,
        CircuitBreakerService circuitBreaker)
    {
        _configuration = configuration;
        _logger = logger;
        _serviceProvider = serviceProvider;
        _universeBuilder = universeBuilder;
        _validator = validator;
        _blueprint = blueprint;
        _circuitBreaker = circuitBreaker;

        _apiKey = configuration["Alpaca:Key"] ?? "";
        _apiSecret = configuration["Alpaca:Secret"] ?? "";
        _streamUrl = configuration["Alpaca:StreamUrl"] ?? "wss://stream.data.alpaca.markets/v2/iex";
        _tapeWindowSeconds = configuration.GetValue("Alpaca:TapeWindowSeconds", 3);
        _zScoreThreshold = configuration.GetValue("Alpaca:ZScoreThreshold", 2.0m);
        _vwapHoldSeconds = configuration.GetValue("Alpaca:VwapHoldSeconds", 5);
        _vwapHoldPrints = configuration.GetValue("Alpaca:VwapHoldPrints", 10);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (string.IsNullOrEmpty(_apiKey) || string.IsNullOrEmpty(_apiSecret))
        {
            _logger.LogWarning("Alpaca API credentials not configured. AlpacaStreamClient is disabled.");
            return;
        }

        // Initial universe load
        _subscribedSymbols = await _universeBuilder.GetActiveUniverseAsync(stoppingToken);

        _logger.LogInformation("AlpacaStreamClient starting with {Count} symbols", _subscribedSymbols.Count);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await ConnectAndStreamAsync(stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(ex, "Stream error, will reconnect");
                _reconnectAttempts++;

                if (_reconnectAttempts > MaxReconnectAttempts)
                {
                    _circuitBreaker.Suspend(TimeSpan.FromMinutes(30), "Max reconnect attempts exceeded");
                    _reconnectAttempts = 0;
                }

                var delay = TimeSpan.FromSeconds(Math.Min(Math.Pow(2, _reconnectAttempts), 60));
                await Task.Delay(delay, stoppingToken);
            }
        }
    }

    private async Task ConnectAndStreamAsync(CancellationToken stoppingToken)
    {
        _webSocket = new ClientWebSocket();
        await _webSocket.ConnectAsync(new Uri(_streamUrl), stoppingToken);
        _logger.LogInformation("WebSocket connected to {Url}", _streamUrl);

        // Wait for welcome message before authenticating
        var buffer = new byte[8192];
        var welcomeResult = await _webSocket.ReceiveAsync(buffer, stoppingToken);
        var welcomeJson = Encoding.UTF8.GetString(buffer, 0, welcomeResult.Count);
        _logger.LogInformation("Received welcome: {Json}", welcomeJson);

        // Authenticate
        await AuthenticateAsync(stoppingToken);

        // Wait for auth response
        var authResult = await _webSocket.ReceiveAsync(buffer, stoppingToken);
        var authJson = Encoding.UTF8.GetString(buffer, 0, authResult.Count);
        _logger.LogInformation("Auth response: {Json}", authJson);
        
        if (authJson.Contains("\"code\":40") || authJson.Contains("auth failed"))
        {
            _logger.LogError("Authentication failed. Check your Alpaca API keys.");
            throw new InvalidOperationException("Alpaca authentication failed");
        }

        // Subscribe to symbols
        await SubscribeAsync(_subscribedSymbols, stoppingToken);

        _reconnectAttempts = 0;
        _lastMessageTime = DateTime.UtcNow;

        // Start feed lag monitor
        _ = Task.Run(() => MonitorFeedLagAsync(stoppingToken), stoppingToken);

        // Message receive loop
        while (_webSocket.State == WebSocketState.Open && !stoppingToken.IsCancellationRequested)
        {
            // Check for universe rebuild
            if (_universeBuilder.ShouldRebuildNow(DateTime.UtcNow))
            {
                var newUniverse = await _universeBuilder.BuildUniverseAsync(stoppingToken);
                if (!newUniverse.SequenceEqual(_subscribedSymbols))
                {
                    await ResubscribeAsync(newUniverse, stoppingToken);
                }
            }

            var result = await _webSocket.ReceiveAsync(buffer, stoppingToken);

            if (result.MessageType == WebSocketMessageType.Close)
            {
                _logger.LogWarning("WebSocket closed by server");
                break;
            }

            _lastMessageTime = DateTime.UtcNow;
            var json = Encoding.UTF8.GetString(buffer, 0, result.Count);

            try
            {
                await ProcessMessageAsync(json, stoppingToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error processing message: {Json}", json.Substring(0, Math.Min(200, json.Length)));
            }
        }
    }

    private async Task AuthenticateAsync(CancellationToken ct)
    {
        var authMsg = JsonSerializer.Serialize(new
        {
            action = "auth",
            key = _apiKey,
            secret = _apiSecret
        });

        await SendAsync(authMsg, ct);
        _logger.LogDebug("Auth message sent");
    }

    private async Task SubscribeAsync(IReadOnlyCollection<string> symbols, CancellationToken ct)
    {
        if (!symbols.Any()) return;

        var subMsg = JsonSerializer.Serialize(new
        {
            action = "subscribe",
            trades = symbols.ToArray(),
            quotes = symbols.ToArray()
        });

        await SendAsync(subMsg, ct);
        _subscribedSymbols = symbols;
        _logger.LogInformation("Subscribed to {Count} symbols", symbols.Count);
    }

    private async Task ResubscribeAsync(IReadOnlyCollection<string> newSymbols, CancellationToken ct)
    {
        // Unsubscribe from old symbols
        if (_subscribedSymbols.Any())
        {
            var unsubMsg = JsonSerializer.Serialize(new
            {
                action = "unsubscribe",
                trades = _subscribedSymbols.ToArray(),
                quotes = _subscribedSymbols.ToArray()
            });
            await SendAsync(unsubMsg, ct);
        }

        // Subscribe to new symbols
        await SubscribeAsync(newSymbols, ct);
    }

    private async Task SendAsync(string message, CancellationToken ct)
    {
        if (_webSocket?.State != WebSocketState.Open) return;

        var bytes = Encoding.UTF8.GetBytes(message);
        await _webSocket.SendAsync(bytes, WebSocketMessageType.Text, true, ct);
    }

    private async Task ProcessMessageAsync(string json, CancellationToken ct)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        if (root.ValueKind != JsonValueKind.Array) return;

        foreach (var msg in root.EnumerateArray())
        {
            if (!msg.TryGetProperty("T", out var typeElem)) continue;
            var msgType = typeElem.GetString();

            switch (msgType)
            {
                case "t": // Trade
                    await ProcessTradeAsync(msg, ct);
                    break;
                case "q": // Quote
                    ProcessQuote(msg);
                    break;
                case "success":
                    _logger.LogDebug("Alpaca: {Msg}", msg.GetProperty("msg").GetString());
                    break;
                case "error":
                    _logger.LogError("Alpaca error: {Code} {Msg}",
                        msg.GetProperty("code").GetInt32(),
                        msg.GetProperty("msg").GetString());
                    break;
            }
        }
    }

    private void ProcessQuote(JsonElement msg)
    {
        var symbol = msg.GetProperty("S").GetString();
        if (string.IsNullOrEmpty(symbol)) return;

        var bidPrice = msg.GetProperty("bp").GetDecimal();
        var askPrice = msg.GetProperty("ap").GetDecimal();
        var bidSize = msg.GetProperty("bs").GetDecimal();
        var askSize = msg.GetProperty("as").GetDecimal();

        var state = _symbolStates.GetOrAdd(symbol, _ => new SymbolState());
        state.UpdateQuote(bidPrice, askPrice, bidSize, askSize);
    }

    private async Task ProcessTradeAsync(JsonElement msg, CancellationToken ct)
    {
        var symbol = msg.GetProperty("S").GetString();
        if (string.IsNullOrEmpty(symbol)) return;

        var price = msg.GetProperty("p").GetDecimal();
        var size = msg.GetProperty("s").GetDecimal();
        var timestamp = msg.GetProperty("t").GetDateTime();

        var state = _symbolStates.GetOrAdd(symbol, _ => new SymbolState());
        state.AddTrade(price, size, timestamp, _tapeWindowSeconds);

        // Check if circuit breaker is active
        if (_circuitBreaker.IsSuspended()) return;

        // Build market data models from real-time state
        var (orderBook, tapeData, vwapData, spread) = state.BuildMarketData(_vwapHoldSeconds, _vwapHoldPrints);

        // Calculate tape z-score
        var tapeZScore = state.CalculateTapeZScore();

        // Check signal validity with time-aware, anti-spoofing validation
        if (!_validator.IsValidSetup(orderBook, tapeData, vwapData, spread, DateTime.UtcNow, state.PreviousSpread))
        {
            state.PreviousSpread = spread;
            return;
        }

        // Additional z-score check
        if (tapeZScore < _zScoreThreshold)
        {
            state.PreviousSpread = spread;
            return;
        }

        // Circuit breaker spread/tape check
        if (_circuitBreaker.ShouldThrottle(spread, tapeData.PrintsPerSecond, DateTime.UtcNow))
        {
            state.PreviousSpread = spread;
            return;
        }

        _logger.LogInformation("🎯 Real-time setup detected for {Symbol}! Spread={Spread:F4}, ZScore={ZScore:F2}",
            symbol, spread, tapeZScore);

        // Generate and save signal
        try
        {
            var score = _validator.CalculateLiquidityScore(orderBook, tapeData, vwapData, spread);
            var signal = _blueprint.Generate(symbol, price, vwapData.VwapPrice, spread, score);

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
            state.PreviousSpread = spread;
        }
    }

    private async Task MonitorFeedLagAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested && _webSocket?.State == WebSocketState.Open)
        {
            await Task.Delay(TimeSpan.FromSeconds(1), ct);

            var lag = DateTime.UtcNow - _lastMessageTime;
            if (lag.TotalSeconds > FeedLagThresholdSeconds)
            {
                _logger.LogWarning("Feed lag detected: {Seconds}s since last message", lag.TotalSeconds);
                _circuitBreaker.Suspend(TimeSpan.FromMinutes(1), $"Feed lag {lag.TotalSeconds:F1}s");
            }
        }
    }
}

/// <summary>
/// Maintains rolling market state for a single symbol.
/// </summary>
public class SymbolState
{
    private readonly object _lock = new();
    private readonly List<(decimal Price, decimal Size, DateTime Time)> _recentTrades = new();
    private readonly List<decimal> _historicalPrintsPerSecond = new();

    // Quote state
    public decimal BidPrice { get; private set; }
    public decimal AskPrice { get; private set; }
    public decimal BidSize { get; private set; }
    public decimal AskSize { get; private set; }
    public decimal? PreviousSpread { get; set; }

    // VWAP state
    private decimal _cumulativeVwapNumerator;
    private decimal _cumulativeVolume;
    private DateTime? _vwapReclaimStart;
    private int _vwapReclaimPrints;

    public void UpdateQuote(decimal bidPrice, decimal askPrice, decimal bidSize, decimal askSize)
    {
        lock (_lock)
        {
            BidPrice = bidPrice;
            AskPrice = askPrice;
            BidSize = bidSize;
            AskSize = askSize;
        }
    }

    public void AddTrade(decimal price, decimal size, DateTime timestamp, int windowSeconds)
    {
        lock (_lock)
        {
            _recentTrades.Add((price, size, timestamp));

            // Update VWAP
            _cumulativeVwapNumerator += price * size;
            _cumulativeVolume += size;

            // Prune old trades outside window
            var cutoff = timestamp.AddSeconds(-windowSeconds);
            _recentTrades.RemoveAll(t => t.Time < cutoff);

            // Track prints per second for z-score history
            var pps = CalculatePrintsPerSecond();
            _historicalPrintsPerSecond.Add(pps);
            if (_historicalPrintsPerSecond.Count > 1000)
            {
                _historicalPrintsPerSecond.RemoveAt(0);
            }

            // Track VWAP hold
            var vwap = GetVwap();
            if (price >= vwap)
            {
                _vwapReclaimStart ??= timestamp;
                _vwapReclaimPrints++;
            }
            else
            {
                _vwapReclaimStart = null;
                _vwapReclaimPrints = 0;
            }
        }
    }

    public decimal GetVwap()
    {
        lock (_lock)
        {
            return _cumulativeVolume > 0 ? _cumulativeVwapNumerator / _cumulativeVolume : 0;
        }
    }

    public decimal CalculatePrintsPerSecond()
    {
        lock (_lock)
        {
            if (_recentTrades.Count < 2) return 0;

            var window = _recentTrades.Max(t => t.Time) - _recentTrades.Min(t => t.Time);
            return window.TotalSeconds > 0 ? _recentTrades.Count / (decimal)window.TotalSeconds : 0;
        }
    }

    public decimal CalculateTapeZScore()
    {
        lock (_lock)
        {
            if (_historicalPrintsPerSecond.Count < 30) return 0;

            var current = CalculatePrintsPerSecond();
            var median = GetMedian(_historicalPrintsPerSecond);
            var iqr = GetIQR(_historicalPrintsPerSecond);

            // Use robust z-score: (x - median) / (IQR / 1.35)
            var robustStdDev = iqr / 1.35m;
            return robustStdDev > 0 ? (current - median) / robustStdDev : 0;
        }
    }

    public (OrderBook, TapeData, VwapData, decimal) BuildMarketData(int vwapHoldSeconds, int vwapHoldPrints)
    {
        lock (_lock)
        {
            var spread = AskPrice > 0 && BidPrice > 0 ? (AskPrice - BidPrice) / AskPrice : 0.02m;
            var bidAskRatio = AskSize > 0 ? BidSize / AskSize : 1m;

            var orderBook = new OrderBook
            {
                BidAskRatio = bidAskRatio,
                TotalBidSize = BidSize,
                TotalAskSize = AskSize,
                Spread = spread,
                Timestamp = DateTime.UtcNow
            };

            var pps = CalculatePrintsPerSecond();
            var lastSize = _recentTrades.Any() ? _recentTrades.Last().Size : 100m;
            var tapeData = new TapeData
            {
                PrintsPerSecond = pps,
                LastPrintSize = lastSize,
                Timestamp = DateTime.UtcNow
            };

            var currentPrice = _recentTrades.Any() ? _recentTrades.Last().Price : 0;
            var vwap = GetVwap();

            // VWAP hold requirement: must be above VWAP for N seconds OR N prints
            var holdDuration = _vwapReclaimStart.HasValue
                ? (DateTime.UtcNow - _vwapReclaimStart.Value).TotalSeconds
                : 0;
            var hasReclaim = currentPrice >= vwap &&
                             (holdDuration >= vwapHoldSeconds || _vwapReclaimPrints >= vwapHoldPrints);

            var vwapData = new VwapData
            {
                CurrentPrice = currentPrice,
                VwapPrice = vwap,
                HasReclaim = hasReclaim
            };

            return (orderBook, tapeData, vwapData, spread);
        }
    }

    private static decimal GetMedian(List<decimal> values)
    {
        var sorted = values.OrderBy(v => v).ToList();
        var mid = sorted.Count / 2;
        return sorted.Count % 2 == 0
            ? (sorted[mid - 1] + sorted[mid]) / 2
            : sorted[mid];
    }

    private static decimal GetIQR(List<decimal> values)
    {
        var sorted = values.OrderBy(v => v).ToList();
        var q1Index = sorted.Count / 4;
        var q3Index = sorted.Count * 3 / 4;
        return sorted[q3Index] - sorted[q1Index];
    }
}
