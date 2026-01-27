using IBApi;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Configuration;
using RamStockAlerts.Engine;
using RamStockAlerts.Models;
using RamStockAlerts.Services;
using RamStockAlerts.Services.Signals;
using RamStockAlerts.Universe;
using System.Collections.Concurrent;
using RamStockAlerts.Services.Universe;
using System.Threading;

namespace RamStockAlerts.Feeds;

/// <summary>
/// Interactive Brokers Level-II Market Data Client
/// 
/// Subscribes to:
/// - Market Depth (L2 order book snapshot, 10 levels)
/// - Level 1 tape via market data (LAST/LAST_SIZE)
/// - Tick-by-Tick Data (Last) for focus symbols
/// 
/// Purpose: Feed OrderFlowMetrics with real order-book imbalances and tape acceleration
/// </summary>
public class IBkrMarketDataClient : BackgroundService
{
    private readonly ILogger<IBkrMarketDataClient> _logger;
    private readonly IConfiguration _configuration;
    private readonly UniverseService _universeService;
    private readonly MarketDataSubscriptionManager _subscriptionManager;
    private readonly OrderFlowMetrics _metrics;
    private readonly SignalCoordinator _signalCoordinator;
    private readonly PreviewSignalEmitter _previewSignalEmitter;
    private readonly ContractClassificationService _classificationService;
    private readonly DepthEligibilityCache _depthEligibilityCache;
    
    private EClientSocket? _eClientSocket;
    private EReaderSignal? _readerSignal;
    private IBkrWrapperImpl? _wrapper;
    
    private readonly ConcurrentDictionary<int, string> _tickerIdMap = new();
    private readonly ConcurrentDictionary<string, OrderBookState> _orderBooks = new();
    private readonly ConcurrentDictionary<string, MarketDataSubscription> _activeSubscriptions = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _subscriptionLock = new(1, 1);
    private readonly IRequestIdSource _requestIdSource;
    private int _nextRequestId = 1000;
    
    /// <summary>
    /// Time to wait for L1/tape receipt after initial subscription before falling back to SMART (ms).
    /// Default: 15 seconds. Configurable via MarketData:L1ReceiptTimeoutMs
    /// </summary>
    private int _l1ReceiptTimeoutMs = 15_000;
    
    /// <summary>
    /// Time to wait for tick-by-tick receipt after initial subscription before falling back to SMART (ms).
    /// Default: 15 seconds. Configurable via MarketData:TickByTickReceiptTimeoutMs
    /// </summary>
    private int _tickByTickReceiptTimeoutMs = 15_000;

    /// <summary>
    /// Reconnect configuration: exponential backoff (2s, 4s, 8s, 16s, 32s, max 60s), max 5 attempts
    /// </summary>
    private int _reconnectMaxAttempts = 5;
    private int _reconnectMaxDelayMs = 60_000;
    
    /// <summary>
    /// Tracks in-flight reconnect operation to prevent concurrent reconnects
    /// </summary>
    private bool _isReconnecting = false;
    private readonly SemaphoreSlim _reconnectLock = new(1, 1);
    
    public IBkrMarketDataClient(
        ILogger<IBkrMarketDataClient> logger,
        IConfiguration configuration,
        UniverseService universeService,
        MarketDataSubscriptionManager subscriptionManager,
        OrderFlowMetrics metrics,
        SignalCoordinator signalCoordinator,
        PreviewSignalEmitter previewSignalEmitter,
        ContractClassificationService classificationService,
        DepthEligibilityCache depthEligibilityCache,
        IRequestIdSource requestIdSource)
    {
        _logger = logger;
        _configuration = configuration;
        _universeService = universeService;
        _subscriptionManager = subscriptionManager;
        _metrics = metrics;
        _signalCoordinator = signalCoordinator;
        _previewSignalEmitter = previewSignalEmitter;
        _classificationService = classificationService;
        _depthEligibilityCache = depthEligibilityCache;
        _requestIdSource = requestIdSource;
        
        // Initialize receipt timeout configuration
        _l1ReceiptTimeoutMs = Math.Max(5_000, configuration.GetValue("MarketData:L1ReceiptTimeoutMs", 15_000));
        _tickByTickReceiptTimeoutMs = Math.Max(5_000, configuration.GetValue("MarketData:TickByTickReceiptTimeoutMs", 15_000));
        _reconnectMaxAttempts = configuration.GetValue("IBKR:ReconnectMaxAttempts", 5);
        _reconnectMaxDelayMs = configuration.GetValue("IBKR:ReconnectMaxDelayMs", 60_000);
    }

    public override async Task StartAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("[IBKR] StartAsync entered - hosted service starting");
        await base.StartAsync(cancellationToken);
        _logger.LogInformation("[IBKR] StartAsync completed - ExecuteAsync launched");
    }
    
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            _logger.LogInformation("[IBKR] Initializing market data client...");
            
            // Create socket and wrapper
            _wrapper = new IBkrWrapperImpl(
                _logger,
                _tickerIdMap,
                _orderBooks,
                _metrics,
                _signalCoordinator,
                _previewSignalEmitter,
                IsTickByTickActive,
                _subscriptionManager.RecordActivity,
                _subscriptionManager.RecordTapeReceipt,
                HandleIbkrError,
                MarkDepthEligible,
                _subscriptionManager.RecordDepthSubscribeUpdateReceived);
            _readerSignal = new EReaderMonitorSignal();
            _eClientSocket = new EClientSocket(_wrapper, _readerSignal);
            
            // Connect to TWS
            var host = _configuration["IBKR:Host"] ?? "127.0.0.1";
            var port = _configuration.GetValue<int?>("IBKR:Port") ?? 7496; // default to TWS live port (7497 = paper)
            var clientId = _configuration.GetValue<int?>("IBKR:ClientId") ?? 1;

            _eClientSocket.eConnect(host, port, clientId);
            
            if (!_eClientSocket.IsConnected())
            {
                _logger.LogError("[IBKR] Failed to connect to TWS at {Host}:{Port}", host, port);
                return;
            }
            
            _logger.LogInformation("[IBKR] Connected to TWS at {Host}:{Port}", host, port);

            // Start API (required by some connection modes)
            _eClientSocket.startApi();
            
            // Start message processing loop
            _ = Task.Run(() => ProcessMessages(_eClientSocket, _readerSignal, stoppingToken), stoppingToken);
            
            // Start fallback monitoring loop
            _ = Task.Run(() => MonitorExchangeFallbacksAsync(stoppingToken), stoppingToken);
            
            _logger.LogInformation("[IBKR] Market data client ready. Managing subscriptions...");

            var refreshInterval = TimeSpan.FromMinutes(5);
            while (!stoppingToken.IsCancellationRequested)
            {
                IReadOnlyList<string> universe;
                try
                {
                    universe = await _universeService.GetUniverseAsync(stoppingToken);
                }
                catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
                {
                    _logger.LogError(ex, "[IBKR] Universe refresh failed.");
                    await Task.Delay(refreshInterval, stoppingToken);
                    continue;
                }

                if (universe.Count == 0)
                {
                    _logger.LogWarning("[IBKR] Universe empty. Holding current subscriptions.");
                }

                await _subscriptionManager.ApplyUniverseAsync(
                    universe,
                    SubscribeSymbolAsync,
                    UnsubscribeSymbolAsync,
                    EnableTickByTickAsync,
                    DisableTickByTickAsync,
                    DisableDepthAsync,
                    stoppingToken);

                LogMarketDataFeedHeartbeat(refreshInterval);

                await Task.Delay(refreshInterval, stoppingToken);
            }
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("[IBKR] Market data client stopping");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[IBKR] Unhandled error in market data client");
        }
        finally
        {
            Cleanup();
        }
    }

    /// <summary>
    /// Select L1 exchange: primary first (if available) → fallback to SMART.
    /// Allowed primary exchanges: NASDAQ, NYSE
    /// </summary>
    private string SelectL1Exchange(ContractClassification? classification)
    {
        // if (classification?.PrimaryExchange != null)
        // {
        //     var primary = classification.PrimaryExchange.Trim().ToUpperInvariant();
        //     if (primary == "NASDAQ" || primary == "NYSE" || primary == "AMEX" || primary == "CBOE" || primary == "BOX")
        //     {
        //         return primary;
        //     }
        // }
        
        return "SMART";
    }

    public async Task<MarketDataSubscription?> SubscribeSymbolAsync(string symbol, bool requestDepth, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(symbol))
        {
            return null;
        }

        if (_eClientSocket?.IsConnected() != true)
        {
            _logger.LogWarning("[IBKR] Subscribe skipped for {Symbol}: not connected.", symbol);
            return null;
        }

        var normalized = symbol.Trim().ToUpperInvariant();

        var enableTape = _configuration.GetValue("MarketData:EnableTape", true);
        var enableDepth = _configuration.GetValue("MarketData:EnableDepth", true);
        requestDepth = requestDepth && enableDepth;
        if (!enableTape && !requestDepth)
        {
            _logger.LogWarning("[IBKR] Subscribe skipped for {Symbol}: depth/tape disabled.", normalized);
            return null;
        }

        // Fetch classification outside lock if depth might be requested
        ContractClassification? classification = null;
        if (requestDepth)
        {
            var fetched = await _classificationService.GetClassificationsAsync(new[] { normalized }, cancellationToken);
            fetched.TryGetValue(normalized, out classification);
        }
        else
        {
            classification = _classificationService.TryGetCached(normalized);
        }

        // Validate depth prerequisites outside lock
        if (requestDepth && (classification is null || classification.ConId <= 0 || string.IsNullOrWhiteSpace(classification.PrimaryExchange)))
        {
            _logger.LogWarning(
                "[IBKR] Depth skipped for {Symbol}: missing contract details (conId/primaryExch).",
                normalized);
            requestDepth = false;
        }

        if (requestDepth && !_depthEligibilityCache.CanRequestDepth(classification, normalized, DateTimeOffset.UtcNow, out var eligibilityState))
        {
            _depthEligibilityCache.LogSkipOnce(classification, normalized, eligibilityState);
            requestDepth = false;
        }

        // All subscription state decisions happen inside the lock
        var depthAttempted = false;
        await _subscriptionLock.WaitAsync(cancellationToken);
        int? mktDataRequestId = null;
        int? depthRequestId = null;
        string? actualDepthExchange = null;
        try
        {
            // Re-read subscription state inside lock - this is the authoritative view
            var existing = _activeSubscriptions.TryGetValue(normalized, out var existingSub) ? existingSub : null;

            // Decide what action to take based on current state and request
            bool isNewSubscription = existing == null;
            bool isDepthUpgrade = existing != null && requestDepth && !existing.DepthRequestId.HasValue;
            bool shouldReturnExisting = existing != null && (!requestDepth || existing.DepthRequestId.HasValue);

            if (shouldReturnExisting)
            {
                _logger.LogDebug(
                    "[IBKR] SubscribeSymbolAsync: symbol={Symbol}, requestDepth={RequestDepth}, existing: mktDataId={MktDataId}, depthId={DepthId} - returning existing",
                    normalized,
                    requestDepth,
                    existing!.MktDataRequestId,
                    existing.DepthRequestId);
                return existing;
            }

            // If requestDepth became false after validation, and we have existing subscription, return it unchanged
            if (existing != null && !requestDepth)
            {
                _logger.LogDebug(
                    "[IBKR] SubscribeSymbolAsync: symbol={Symbol}, requestDepth=false after checks - returning existing unchanged",
                    normalized);
                return existing;
            }

            _logger.LogInformation(
                "[IBKR] SubscribeSymbolAsync: symbol={Symbol}, requestDepth={RequestDepth}, action={Action}",
                normalized,
                requestDepth,
                isNewSubscription ? "NEW" : isDepthUpgrade ? "UPGRADE_TO_DEPTH" : "UNKNOWN");

            // Select L1 exchange: primary first → fallback to SMART
            var l1Exchange = SelectL1Exchange(classification);
            var baseContract = new Contract
            {
                Symbol = normalized,
                SecType = classification?.SecType ?? "STK",
                Exchange = l1Exchange,
                Currency = classification?.Currency ?? "USD"
            };
            
            _logger.LogInformation(
                "[IBKR] MarketDataExchangePolicy: symbol={Symbol} l1Exchange={L1Exchange} primaryExchange={PrimaryExchange} policy=primary-first-smart-fallback",
                normalized,
                l1Exchange,
                classification?.PrimaryExchange ?? "unknown");

            // Request tape only for NEW subscriptions
            if (isNewSubscription && enableTape)
            {
                mktDataRequestId = Interlocked.Increment(ref _nextRequestId);
                _eClientSocket.reqMktData(mktDataRequestId.Value, baseContract, string.Empty, false, false, null);
                _tickerIdMap[mktDataRequestId.Value] = normalized;
                
                _logger.LogDebug(
                    "[IBKR] L1Request: symbol={Symbol} mktDataId={MktDataId} exchange={Exchange} subscribedAt={Now}",
                    normalized,
                    mktDataRequestId.Value,
                    l1Exchange,
                    DateTimeOffset.UtcNow);
            }
            else if (isDepthUpgrade)
            {
                // Reuse existing tape subscription
                mktDataRequestId = existing!.MktDataRequestId;
            }

            // Request depth if needed
            if (requestDepth)
            {
                _subscriptionManager.RecordDepthSubscribeAttempt(normalized);
                depthAttempted = true;
                var depthRows = Math.Clamp(_configuration.GetValue("MarketData:DepthRows", 5), 1, 10);
                depthRequestId = Interlocked.Increment(ref _nextRequestId);
                var depthContract = BuildDepthContractForDepth(normalized, classification);
                
                // Capture the actual exchange used for depth request
                actualDepthExchange = depthContract.Exchange;
                
                _logger.LogInformation(
                    "[IBKR] DepthRequest: symbol={Symbol}, conId={ConId}, exchange={Exchange}, secType={SecType}",
                    normalized,
                    classification?.ConId ?? 0,
                    actualDepthExchange,
                    depthContract.SecType);
                
                LogDepthRequest(depthContract, classification, depthRows, isSmart: false);
                _eClientSocket.reqMarketDepth(depthRequestId.Value, depthContract, depthRows, false, null);
                _tickerIdMap[depthRequestId.Value] = normalized;
            }

            // Ensure we have at least one request type
            if (mktDataRequestId is null && depthRequestId is null)
            {
                _logger.LogWarning("[IBKR] No requests issued for {Symbol}", normalized);
                return existing; // Return existing if available, otherwise null
            }

            _orderBooks.AddOrUpdate(
                normalized,
                _ => new OrderBookState { Symbol = normalized },
                (_, existingBook) => existingBook);

            // Create or update subscription record
            MarketDataSubscription newSubscription;
            var now = DateTimeOffset.UtcNow;
            if (isNewSubscription)
            {
                // New subscription - create full record
                newSubscription = new MarketDataSubscription(
                    normalized,
                    mktDataRequestId,
                    depthRequestId,
                    null, // No tick-by-tick yet
                    actualDepthExchange,
                    l1Exchange,                    // L1 exchange (primary or SMART)
                    null,                          // TickByTick exchange set on EnableTickByTickAsync
                    mktDataRequestId.HasValue ? now : null,  // Record subscription time for fallback monitoring
                    null);
            }
            else if (isDepthUpgrade)
            {
                // Depth upgrade - clone existing and only update depth fields
                newSubscription = new MarketDataSubscription(
                    normalized,
                    existing!.MktDataRequestId,     // Preserve existing
                    depthRequestId,                 // New depth request
                    existing.TickByTickRequestId,   // Preserve existing
                    actualDepthExchange,            // Actual exchange from depth contract
                    existing.MktDataExchange,       // Preserve L1 exchange
                    existing.TickByTickExchange,    // Preserve tick-by-tick exchange
                    existing.MktDataFirstReceiptMs,    // Preserve L1 receipt time
                    existing.TickByTickFirstReceiptMs);  // Preserve tick-by-tick receipt time
            }
            else
            {
                _logger.LogWarning("[IBKR] Unexpected state for {Symbol}", normalized);
                return existing;
            }

            _activeSubscriptions[normalized] = newSubscription;

            _logger.LogInformation(
                "[IBKR] SubscribeSymbolAsync EXIT: symbol={Symbol}, mktDataId={MktDataId}, depthId={DepthId}, tickByTickId={TickByTickId}, action={Action}",
                normalized,
                newSubscription.MktDataRequestId,
                newSubscription.DepthRequestId,
                newSubscription.TickByTickRequestId,
                isNewSubscription ? "NEW" : "UPGRADE");

            return newSubscription;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[IBKR] Error subscribing to {Symbol}", normalized);
            if (depthAttempted)
            {
                _subscriptionManager.RecordDepthSubscribeFailure(null, ex.Message);
            }
            if (_eClientSocket?.IsConnected() == true)
            {
                if (mktDataRequestId.HasValue)
                {
                    _eClientSocket.cancelMktData(mktDataRequestId.Value);
                }

                if (depthRequestId.HasValue)
                {
                    _eClientSocket.cancelMktDepth(depthRequestId.Value, false);
                }
            }

            if (mktDataRequestId.HasValue)
            {
                _tickerIdMap.TryRemove(mktDataRequestId.Value, out _);
            }

            if (depthRequestId.HasValue)
            {
                _tickerIdMap.TryRemove(depthRequestId.Value, out _);
            }
            return null;
        }
        finally
        {
            _subscriptionLock.Release();
        }
    }

    public async Task<int?> EnableTickByTickAsync(string symbol, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(symbol))
        {
            return null;
        }

        if (_eClientSocket?.IsConnected() != true)
        {
            _logger.LogWarning("[IBKR] Tick-by-tick enable skipped for {Symbol}: not connected.", symbol);
            return null;
        }

        var normalized = symbol.Trim().ToUpperInvariant();
        if (!_activeSubscriptions.TryGetValue(normalized, out var existing))
        {
            _logger.LogWarning("[IBKR] Tick-by-tick enable skipped for {Symbol}: no base subscription.", normalized);
            return null;
        }

        if (existing.TickByTickRequestId.HasValue)
        {
            return existing.TickByTickRequestId;
        }

        await _subscriptionLock.WaitAsync(cancellationToken);
        int? requestId = null;
        try
        {
            if (_activeSubscriptions.TryGetValue(normalized, out existing) && existing.TickByTickRequestId.HasValue)
            {
                return existing.TickByTickRequestId;
            }

            if (!_activeSubscriptions.TryGetValue(normalized, out existing))
            {
                return null;
            }

            // Get classification for exchange selection
            var classification = _classificationService.TryGetCached(normalized);
            var tbtExchange = SelectL1Exchange(classification); // Use same exchange selection policy as L1
            
            var contract = new Contract
            {
                Symbol = normalized,
                SecType = "STK",
                Exchange = tbtExchange,
                Currency = "USD"
            };

            requestId = Interlocked.Increment(ref _nextRequestId);
            _eClientSocket.reqTickByTickData(requestId.Value, contract, "Last", 0, false);
            _tickerIdMap[requestId.Value] = normalized;

            var now = DateTimeOffset.UtcNow;
            var updated = existing with 
            { 
                TickByTickRequestId = requestId.Value,
                TickByTickExchange = tbtExchange,
                TickByTickFirstReceiptMs = now
            };
            _activeSubscriptions[normalized] = updated;

            _logger.LogInformation(
                "[IBKR] Enabled tick-by-tick for {Symbol} tickByTickId={TickByTickId} exchange={Exchange} primaryExchange={PrimaryExchange} policy=primary-first-smart-fallback",
                normalized,
                requestId,
                tbtExchange,
                classification?.PrimaryExchange ?? "unknown");
            return requestId;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[IBKR] Error enabling tick-by-tick for {Symbol} - possible subscription limit reached", normalized);
            if (requestId.HasValue && _eClientSocket?.IsConnected() == true)
            {
                _eClientSocket.cancelTickByTickData(requestId.Value);
            }

            if (requestId.HasValue)
            {
                _tickerIdMap.TryRemove(requestId.Value, out _);
            }
            return null;
        }
        finally
        {
            _subscriptionLock.Release();
        }
    }

    public async Task<bool> DisableTickByTickAsync(string symbol, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(symbol))
        {
            return false;
        }

        var normalized = symbol.Trim().ToUpperInvariant();
        if (!_activeSubscriptions.TryGetValue(normalized, out var existing) || !existing.TickByTickRequestId.HasValue)
        {
            return false;
        }

        await _subscriptionLock.WaitAsync(cancellationToken);
        try
        {
            if (!_activeSubscriptions.TryGetValue(normalized, out existing) || !existing.TickByTickRequestId.HasValue)
            {
                return false;
            }

            if (_eClientSocket?.IsConnected() == true)
            {
                _eClientSocket.cancelTickByTickData(existing.TickByTickRequestId.Value);
            }

            _tickerIdMap.TryRemove(existing.TickByTickRequestId.Value, out _);
            _activeSubscriptions[normalized] = existing with { TickByTickRequestId = null };

            _logger.LogInformation(
                "[IBKR] Disabled tick-by-tick for {Symbol} tickByTickId={TickByTickId}",
                normalized,
                existing.TickByTickRequestId);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[IBKR] Error disabling tick-by-tick for {Symbol}", normalized);
            return false;
        }
        finally
        {
            _subscriptionLock.Release();
        }
    }

    public async Task<bool> DisableDepthAsync(string symbol, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(symbol))
        {
            return false;
        }

        var normalized = symbol.Trim().ToUpperInvariant();
        if (!_activeSubscriptions.TryGetValue(normalized, out var existing) || !existing.DepthRequestId.HasValue)
        {
            return false;
        }

        await _subscriptionLock.WaitAsync(cancellationToken);
        try
        {
            if (!_activeSubscriptions.TryGetValue(normalized, out existing) || !existing.DepthRequestId.HasValue)
            {
                return false;
            }

            if (_eClientSocket?.IsConnected() == true)
            {
                _eClientSocket.cancelMktDepth(existing.DepthRequestId.Value, false);
            }

            _tickerIdMap.TryRemove(existing.DepthRequestId.Value, out _);
            _activeSubscriptions[normalized] = existing with { DepthRequestId = null };

            _logger.LogDebug(
                "[IBKR] Disabled depth for {Symbol} depthId={DepthId}",
                normalized,
                existing.DepthRequestId);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[IBKR] Error disabling depth for {Symbol}", normalized);
            return false;
        }
        finally
        {
            _subscriptionLock.Release();
        }
    }

    public async Task<bool> UnsubscribeSymbolAsync(string symbol, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(symbol))
        {
            return false;
        }

        var normalized = symbol.Trim().ToUpperInvariant();
        if (!_activeSubscriptions.TryGetValue(normalized, out var subscription))
        {
            return false;
        }

        await _subscriptionLock.WaitAsync(cancellationToken);
        try
        {
            if (!_activeSubscriptions.TryRemove(normalized, out subscription))
            {
                return false;
            }

            if (_eClientSocket?.IsConnected() == true)
            {
                if (subscription.MktDataRequestId.HasValue)
                {
                    _eClientSocket.cancelMktData(subscription.MktDataRequestId.Value);
                }

                if (subscription.DepthRequestId.HasValue)
                {
                    _eClientSocket.cancelMktDepth(subscription.DepthRequestId.Value, false);
                }

                if (subscription.TickByTickRequestId.HasValue)
                {
                    _eClientSocket.cancelTickByTickData(subscription.TickByTickRequestId.Value);
                }
            }
            else
            {
                _logger.LogWarning("[IBKR] Unsubscribe for {Symbol} while disconnected.", normalized);
            }

            if (subscription.MktDataRequestId.HasValue)
            {
                _tickerIdMap.TryRemove(subscription.MktDataRequestId.Value, out _);
            }

            if (subscription.DepthRequestId.HasValue)
            {
                _tickerIdMap.TryRemove(subscription.DepthRequestId.Value, out _);
            }

            if (subscription.TickByTickRequestId.HasValue)
            {
                _tickerIdMap.TryRemove(subscription.TickByTickRequestId.Value, out _);
            }

            _orderBooks.TryRemove(normalized, out _);

            _logger.LogInformation(
                "[IBKR] Unsubscribed from {Symbol} mktDataId={MktDataId} depthId={DepthId} tickByTickId={TickByTickId}",
                normalized,
                subscription.MktDataRequestId,
                subscription.DepthRequestId,
                subscription.TickByTickRequestId);

            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[IBKR] Error unsubscribing from {Symbol}", normalized);
            return false;
        }
        finally
        {
            _subscriptionLock.Release();
        }
    }

    /// <summary>
    /// Gets the age in seconds of the last received tick across all subscribed symbols.
    /// Returns null if no ticks have been received yet.
    /// Used for heartbeat/disconnect detection.
    /// </summary>
    public double? GetLastTickAgeSeconds()
    {
        var nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        long? mostRecentTickMs = null;

        foreach (var kvp in _orderBooks)
        {
            var book = kvp.Value;
            
            // Check last trade timestamp
            if (book.RecentTrades.Count > 0)
            {
                var lastTrade = book.RecentTrades.Last();
                if (mostRecentTickMs is null || lastTrade.ReceiptTimestampMs > mostRecentTickMs.Value)
                {
                    mostRecentTickMs = lastTrade.ReceiptTimestampMs;
                }
            }
            
            // Also check last L1 update
            if (book.LastL1RecvMs.HasValue && book.LastL1RecvMs.Value > 0)
            {
                if (mostRecentTickMs is null || book.LastL1RecvMs.Value > mostRecentTickMs.Value)
                {
                    mostRecentTickMs = book.LastL1RecvMs.Value;
                }
            }
            
            // Also check last depth update
            if (book.LastDepthUpdateUtcMs > 0)
            {
                if (mostRecentTickMs is null || book.LastDepthUpdateUtcMs > mostRecentTickMs.Value)
                {
                    mostRecentTickMs = book.LastDepthUpdateUtcMs;
                }
            }
        }

        if (mostRecentTickMs is null)
        {
            return null;
        }

        var ageMs = nowMs - mostRecentTickMs.Value;
        return ageMs / 1000.0;
    }

    private void LogMarketDataFeedHeartbeat(TimeSpan refreshInterval)
    {
        var stats = _subscriptionManager.GetSubscriptionStats();
        var lastTickAge = GetLastTickAgeSeconds();
        var nextRefreshAt = DateTimeOffset.UtcNow + refreshInterval;
        var tickAgeDisplay = lastTickAge.HasValue ? $"{lastTickAge.Value:F1}s" : "no ticks yet";

        _logger.LogInformation(
            "[MarketData] Feed active: tape={TapeCount} subs, depth={DepthCount}, tickByTick={TickCount}, last tick age={TickAge}, next refresh at {NextRefresh:O}.",
            stats.TotalSubscriptions,
            stats.DepthEnabled,
            stats.TickByTickEnabled,
            tickAgeDisplay,
            nextRefreshAt);
    }

    /// <summary>
    /// Initiates a disconnect from IBKR with reconnect logic.
    /// Clears all subscriptions and socket state. Intended to be called before ConnectAsync.
    /// </summary>
    public async Task<bool> DisconnectAsync(CancellationToken cancellationToken)
    {
        try
        {
            if (_eClientSocket?.IsConnected() != true)
            {
                _logger.LogInformation("[IBKR] Already disconnected or socket null.");
                return true;
            }

            var subscriptionCount = _activeSubscriptions.Count;
            _logger.LogWarning("[IBKR] Disconnecting from TWS. Clearing {SubscriptionCount} active subscriptions. IBKR resilience: disconnect phase initiated.", subscriptionCount);

            // Cancel all active subscriptions
            var symbols = _activeSubscriptions.Keys.ToList();
            foreach (var symbol in symbols)
            {
                if (_activeSubscriptions.TryRemove(symbol, out var sub))
                {
                    if (_eClientSocket?.IsConnected() == true)
                    {
                        if (sub.MktDataRequestId.HasValue)
                            _eClientSocket.cancelMktData(sub.MktDataRequestId.Value);
                        if (sub.DepthRequestId.HasValue)
                            _eClientSocket.cancelMktDepth(sub.DepthRequestId.Value, false);
                        if (sub.TickByTickRequestId.HasValue)
                            _eClientSocket.cancelTickByTickData(sub.TickByTickRequestId.Value);
                    }

                    if (sub.MktDataRequestId.HasValue)
                        _tickerIdMap.TryRemove(sub.MktDataRequestId.Value, out _);
                    if (sub.DepthRequestId.HasValue)
                        _tickerIdMap.TryRemove(sub.DepthRequestId.Value, out _);
                    if (sub.TickByTickRequestId.HasValue)
                        _tickerIdMap.TryRemove(sub.TickByTickRequestId.Value, out _);
                }
            }

            // Disconnect socket
            _eClientSocket?.eDisconnect();
            await Task.Delay(500, cancellationToken); // Brief delay to allow cleanup

            _logger.LogWarning(
                "[IBKR] Disconnected from TWS. Cleared {SymbolCount} subscriptions. IBKR resilience: disconnect phase complete.",
                symbols.Count);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[IBKR] Error during disconnect.");
            return false;
        }
    }

    /// <summary>
    /// Reconnects to IBKR with exponential backoff (2s, 4s, 8s, 16s, 32s, max 60s).
    /// Max 5 retry attempts. Logs all attempts and outcomes with resilience context.
    /// </summary>
    public async Task<bool> ConnectAsync(CancellationToken cancellationToken)
    {
        await _reconnectLock.WaitAsync(cancellationToken);
        try
        {
            if (_isReconnecting)
            {
                _logger.LogInformation("[IBKR] Reconnect already in progress.");
                return false;
            }

            if (_eClientSocket?.IsConnected() == true)
            {
                _logger.LogInformation("[IBKR] Already connected.");
                return true;
            }

            _isReconnecting = true;
            var attempt = 0;
            var startTime = DateTimeOffset.UtcNow;

            while (attempt < _reconnectMaxAttempts && !cancellationToken.IsCancellationRequested)
            {
                attempt++;

                // Calculate exponential backoff: 2s, 4s, 8s, 16s, 32s (capped at 60s)
                var delayMs = (int)Math.Min(_reconnectMaxDelayMs, Math.Pow(2, attempt + 1) * 1000);
                
                _logger.LogInformation(
                    "[IBKR] Reconnect attempt {Attempt}/{Max} - exponential backoff {DelayMs}ms. IBKR resilience: reconnect phase active.",
                    attempt,
                    _reconnectMaxAttempts,
                    delayMs);

                await Task.Delay(delayMs, cancellationToken);

                try
                {
                    var host = _configuration["IBKR:Host"] ?? "127.0.0.1";
                    var port = _configuration.GetValue<int?>("IBKR:Port") ?? 7496;
                    var clientId = _configuration.GetValue<int?>("IBKR:ClientId") ?? 1;

                    // Reinitialize socket
                    _wrapper = new IBkrWrapperImpl(
                        _logger,
                        _tickerIdMap,
                        _orderBooks,
                        _metrics,
                        _signalCoordinator,
                        _previewSignalEmitter,
                        IsTickByTickActive,
                        _subscriptionManager.RecordActivity,
                        _subscriptionManager.RecordTapeReceipt,
                        HandleIbkrError,
                        MarkDepthEligible,
                        _subscriptionManager.RecordDepthSubscribeUpdateReceived);

                    _readerSignal = new EReaderMonitorSignal();
                    _eClientSocket = new EClientSocket(_wrapper, _readerSignal);

                    _eClientSocket.eConnect(host, port, clientId);

                    if (!_eClientSocket.IsConnected())
                    {
                        _logger.LogWarning("[IBKR] Connection attempt {Attempt} failed.", attempt);
                        continue;
                    }

                    _eClientSocket.startApi();

                    var elapsedMs = (long)(DateTimeOffset.UtcNow - startTime).TotalMilliseconds;
                    _logger.LogInformation(
                        "[IBKR] Reconnected successfully on attempt {Attempt} after {TotalMs}ms. IBKR resilience: reconnection successful.",
                        attempt,
                        elapsedMs);

                    // Start message processing loop
                    _ = Task.Run(() => ProcessMessages(_eClientSocket, _readerSignal, cancellationToken), cancellationToken);

                    _isReconnecting = false;
                    return true;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "[IBKR] Reconnect attempt {Attempt} failed with exception.", attempt);
                }
            }

            var totalElapsedMs = (long)(DateTimeOffset.UtcNow - startTime).TotalMilliseconds;
            _logger.LogError(
                "[IBKR] Reconnect FAILED after {MaxAttempts} attempts over {TotalMs}ms. IBKR resilience: reconnection exhausted. System will retry via heartbeat.",
                _reconnectMaxAttempts,
                totalElapsedMs);
            _isReconnecting = false;
            return false;
        }
        finally
        {
            _reconnectLock.Release();
        }
    }

    /// <summary>
    /// Re-subscribes to all active symbols after reconnection.
    /// Must be called after ConnectAsync succeeds.
    /// </summary>
    public async Task<int> ReSubscribeActiveSymbolsAsync(CancellationToken cancellationToken)
    {
        if (_eClientSocket?.IsConnected() != true)
        {
            _logger.LogWarning("[IBKR] Cannot re-subscribe: not connected. IBKR resilience: re-subscription blocked.");
            return 0;
        }

        var symbols = _activeSubscriptions.Keys.ToList();
        var resubscribeCount = 0;
        var startTime = DateTimeOffset.UtcNow;

        _logger.LogInformation("[IBKR] Starting re-subscription of {SymbolCount} symbols after reconnect. IBKR resilience: recovery phase initiated.", symbols.Count);

        foreach (var symbol in symbols)
        {
            try
            {
                // Re-subscribe with same parameters as original subscription
                // For simplicity in Phase 4.2, we subscribe to depth (primary need for trading)
                var subscription = await SubscribeSymbolAsync(symbol, requestDepth: true, cancellationToken);
                if (subscription is not null)
                {
                    resubscribeCount++;
                    _logger.LogDebug("[IBKR] Re-subscribed to {Symbol} mktDataId={MktDataId} depthId={DepthId}", symbol, subscription.MktDataRequestId, subscription.DepthRequestId);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[IBKR] Failed to re-subscribe {Symbol}", symbol);
            }
        }

        var elapsedMs = (long)(DateTimeOffset.UtcNow - startTime).TotalMilliseconds;
        _logger.LogInformation(
            "[IBKR] Re-subscription complete: {ResubscribedCount}/{TotalCount} symbols recovered in {ElapsedMs}ms. IBKR resilience: recovery phase result={Result}.",
            resubscribeCount,
            symbols.Count,
            elapsedMs,
            resubscribeCount == symbols.Count ? "SUCCESS" : "PARTIAL");

        return resubscribeCount;
    }

    /// <summary>
    /// Checks if reconnect is currently in progress.
    /// </summary>
    public bool IsReconnecting()
    {
        return _isReconnecting;
    }

    /// <summary>
    /// Checks if currently connected to IBKR TWS/Gateway.
    /// </summary>
    public bool IsConnected()
    {
        return _eClientSocket?.IsConnected() == true;
    }

    private bool IsTickByTickActive(string symbol)
    {
        if (string.IsNullOrWhiteSpace(symbol))
        {
            return false;
        }

        var normalized = symbol.Trim().ToUpperInvariant();
        return _activeSubscriptions.TryGetValue(normalized, out var subscription)
            && subscription.TickByTickRequestId.HasValue;
    }

    private void HandleIbkrError(int requestId, int errorCode, string errorMessage)
    {
        _ = HandleIbkrErrorAsync(requestId, errorCode, errorMessage);
    }

    private async Task HandleIbkrErrorAsync(int requestId, int errorCode, string errorMessage)
    {
        if (errorCode == 10190)
        {
            await ClearRejectedTickByTickAsync(requestId, CancellationToken.None);
        }

        if (errorCode == 10092 && _tickerIdMap.TryGetValue(requestId, out var symbol))
        {
            LogDepthIneligible(symbol, errorCode, errorMessage);
        }

        await _subscriptionManager.HandleIbkrErrorAsync(
            requestId,
            errorCode,
            errorMessage,
            DisableDepthAsync,
            DisableTickByTickAsync,
            EnableTickByTickAsync,
            CancellationToken.None);
        return;
    }

    private async Task ClearRejectedTickByTickAsync(int requestId, CancellationToken cancellationToken)
    {
        if (!_tickerIdMap.TryGetValue(requestId, out var symbol))
        {
            return;
        }

        await _subscriptionLock.WaitAsync(cancellationToken);
        try
        {
            if (!_activeSubscriptions.TryGetValue(symbol, out var subscription))
            {
                return;
            }

            if (subscription.TickByTickRequestId != requestId)
            {
                return;
            }

            _tickerIdMap.TryRemove(requestId, out _);
            _activeSubscriptions[symbol] = subscription with { TickByTickRequestId = null };
        }
        finally
        {
            _subscriptionLock.Release();
        }
    }

    private async Task RetryDepthAsync(int failedRequestId, DepthRetryPlan plan)
    {
        if (_eClientSocket?.IsConnected() != true)
        {
            return;
        }

        if (!_activeSubscriptions.TryGetValue(plan.Symbol, out var existing))
        {
            return;
        }

        if (existing.DepthRequestId != failedRequestId)
        {
            _subscriptionManager.ClearDepthRequest(plan.Symbol, failedRequestId);
            return;
        }

        try
        {
            _eClientSocket.cancelMktDepth(failedRequestId, false);
        }
        catch
        {
        }

        _tickerIdMap.TryRemove(failedRequestId, out _);
        _subscriptionManager.ClearDepthRequest(plan.Symbol, failedRequestId);

        var depthRows = Math.Clamp(_configuration.GetValue("MarketData:DepthRows", 5), 1, 10);
        var depthRequestId = _requestIdSource.NextId();
        var exchange = plan.PrimaryExchange ?? "SMART";
        var retryContract = BuildDepthContract(plan.Symbol, plan, exchange);

        try
        {
            _subscriptionManager.RecordDepthSubscribeAttempt(plan.Symbol);
            LogDepthRequest(retryContract, plan, depthRows, isSmart: false);
            _eClientSocket.reqMarketDepth(depthRequestId, retryContract, depthRows, false, null);
            _tickerIdMap[depthRequestId] = plan.Symbol;

            _activeSubscriptions[plan.Symbol] = existing with { DepthRequestId = depthRequestId, DepthExchange = exchange };
            _subscriptionManager.UpdateDepthRequest(plan.Symbol, depthRequestId, exchange);

            _logger.LogInformation(
                "[IBKR] Retrying depth subscription symbol={Symbol} exchange={Exchange} depthId={DepthId}",
                plan.Symbol,
                exchange,
                depthRequestId);
        }
        catch (Exception ex)
        {
            _subscriptionManager.RecordDepthSubscribeFailure(null, ex.Message);
            _subscriptionManager.MarkDepthUnsupported(plan.Symbol, "DepthUnsupported", DateTimeOffset.UtcNow);
            await DisableDepthAsync(plan.Symbol, CancellationToken.None);
            _logger.LogWarning(ex, "[IBKR] Depth retry failed symbol={Symbol}", plan.Symbol);
        }
    }

    private static Contract BuildDepthContract(string symbol, ContractClassification? classification, string exchange)
    {
        var contract = new Contract
        {
            Symbol = symbol,
            SecType = classification?.SecType ?? "STK",
            Exchange = exchange,
            Currency = classification?.Currency ?? "USD",
            LocalSymbol = classification?.LocalSymbol,
            TradingClass = classification?.TradingClass,
            LastTradeDateOrContractMonth = classification?.LastTradeDateOrContractMonth,
            Multiplier = classification?.Multiplier
        };

        if (classification?.ConId is > 0)
        {
            contract.ConId = classification.ConId;
        }

        if (!string.IsNullOrWhiteSpace(classification?.PrimaryExchange))
        {
            contract.PrimaryExch = classification.PrimaryExchange;
        }

        return contract;
    }

    private static Contract BuildDepthContract(string symbol, DepthRetryPlan plan, string exchange)
    {
        var contract = new Contract
        {
            Symbol = symbol,
            SecType = string.IsNullOrWhiteSpace(plan.SecType) ? "STK" : plan.SecType,
            Exchange = exchange,
            Currency = plan.Currency ?? "USD",
            ConId = plan.ConId
        };

        if (!string.IsNullOrWhiteSpace(plan.PrimaryExchange))
        {
            contract.PrimaryExch = plan.PrimaryExchange;
        }

        return contract;
    }

    private void MarkDepthEligible(string symbol)
    {
        var classification = _classificationService.TryGetCached(symbol);
        _depthEligibilityCache.MarkEligible(classification, symbol);
    }

    internal static string ResolveDepthExchange(ContractClassification? classification)
    {
        var primary = classification?.PrimaryExchange?.Trim().ToUpperInvariant();
        
        // NASDAQ TotalView (Level 2) requires the 'ISLAND' exchange identifier in IBKR API
        if (primary == "NASDAQ")
        {
            return "ISLAND";
        }

        if (!string.IsNullOrWhiteSpace(primary) && primary != "UNKNOWN")
        {
            return primary;
        }

        if (!string.IsNullOrWhiteSpace(classification?.Exchange) && classification.Exchange != "SMART")
        {
            return classification.Exchange.Trim().ToUpperInvariant();
        }

        // Default to SMART if no primary exchange is known, though reqMktDepth will likely fail
        return "SMART";
    }

    internal static Contract BuildDepthContractForDepth(string symbol, ContractClassification? classification)
    {
        var exchange = ResolveDepthExchange(classification);
        return BuildDepthContract(symbol, classification, exchange);
    }


    internal static DepthRequestLogFields BuildDepthRequestLogFields(
        Contract contract,
        string? primaryExchange,
        int depthRows,
        bool isSmart)
    {
        return new DepthRequestLogFields(
            contract.Symbol,
            contract.ConId,
            contract.SecType,
            contract.Exchange,
            primaryExchange ?? contract.PrimaryExch,
            contract.Currency,
            contract.LocalSymbol,
            contract.TradingClass,
            contract.LastTradeDateOrContractMonth,
            contract.Multiplier,
            depthRows,
            isSmart);
    }

    private void LogDepthRequest(Contract contract, ContractClassification? classification, int depthRows, bool isSmart)
    {
        var fields = BuildDepthRequestLogFields(contract, classification?.PrimaryExchange, depthRows, isSmart);
        _logger.LogInformation(
            "DepthRequest: symbol={Symbol} conId={ConId} secType={SecType} exch={Exchange} primaryExch={PrimaryExchange} cur={Currency} localSymbol={LocalSymbol} tradingClass={TradingClass} lastTradeDateOrContractMonth={LastTradeDateOrContractMonth} multiplier={Multiplier} depthRows={DepthRows} isSmart={IsSmart}",
            fields.Symbol,
            fields.ConId,
            fields.SecType,
            fields.Exchange,
            fields.PrimaryExchange,
            fields.Currency,
            fields.LocalSymbol ?? string.Empty,
            fields.TradingClass ?? string.Empty,
            fields.LastTradeDateOrContractMonth ?? string.Empty,
            fields.Multiplier ?? string.Empty,
            fields.DepthRows,
            fields.IsSmart);
    }

    private void LogDepthRequest(Contract contract, DepthRetryPlan plan, int depthRows, bool isSmart)
    {
        var fields = BuildDepthRequestLogFields(contract, plan.PrimaryExchange, depthRows, isSmart);
        _logger.LogInformation(
            "DepthRequest: symbol={Symbol} conId={ConId} secType={SecType} exch={Exchange} primaryExch={PrimaryExchange} cur={Currency} localSymbol={LocalSymbol} tradingClass={TradingClass} lastTradeDateOrContractMonth={LastTradeDateOrContractMonth} multiplier={Multiplier} depthRows={DepthRows} isSmart={IsSmart}",
            fields.Symbol,
            fields.ConId,
            fields.SecType,
            fields.Exchange,
            fields.PrimaryExchange,
            fields.Currency,
            fields.LocalSymbol ?? string.Empty,
            fields.TradingClass ?? string.Empty,
            fields.LastTradeDateOrContractMonth ?? string.Empty,
            fields.Multiplier ?? string.Empty,
            fields.DepthRows,
            fields.IsSmart);
    }

    private void LogDepthIneligible(string symbol, int errorCode, string errorMessage)
    {
        var classification = _classificationService.TryGetCached(symbol);
        var secType = classification?.SecType ?? "STK";
        var conId = classification?.ConId ?? 0;
        var exchange = _activeSubscriptions.TryGetValue(symbol, out var subscription)
            ? subscription.DepthExchange
            : null;
        var primaryExchange = classification?.PrimaryExchange;

        _logger.LogWarning(
            "DepthSubscriptionError: symbol={Symbol} conId={ConId} exch={Exchange} primaryExch={PrimaryExchange} secType={SecType} localSymbol={LocalSymbol} tradingClass={TradingClass} lastTradeDateOrContractMonth={LastTradeDateOrContractMonth} multiplier={Multiplier} code={Code} msg={Msg}. IBKR resilience: depth ineligibility detected. Market data will fall back to L1 only.",
            symbol,
            conId,
            exchange ?? "SMART",
            primaryExchange ?? "n/a",
            secType,
            classification?.LocalSymbol ?? string.Empty,
            classification?.TradingClass ?? string.Empty,
            classification?.LastTradeDateOrContractMonth ?? string.Empty,
            classification?.Multiplier ?? string.Empty,
            errorCode,
            errorMessage);
    }

    internal readonly record struct DepthRequestLogFields(
        string Symbol,
        int ConId,
        string? SecType,
        string? Exchange,
        string? PrimaryExchange,
        string? Currency,
        string? LocalSymbol,
        string? TradingClass,
        string? LastTradeDateOrContractMonth,
        string? Multiplier,
        int DepthRows,
        bool IsSmart);
    
    private void ProcessMessages(EClientSocket socket, EReaderSignal readerSignal, CancellationToken stoppingToken)
    {
        var reader = new EReader(socket, readerSignal);
        reader.Start();

        // Ensure waitForSignal unblocks on shutdown.
        using var _ = stoppingToken.Register(() =>
        {
            try
            {
                readerSignal.issueSignal();
            }
            catch
            {
                // Best-effort only.
            }
        });

        while (!stoppingToken.IsCancellationRequested && socket.IsConnected())
        {
            try
            {
                readerSignal.waitForSignal();
                reader.processMsgs();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[IBKR] Error processing messages");
                Thread.Sleep(1000);
            }
        }
    }
    
    /// <summary>
    /// Monitor active subscriptions for data receipt and trigger fallback to SMART if timeouts occur.
    /// Runs periodically to check L1 and tick-by-tick subscriptions for staleness.
    /// </summary>
    private async Task MonitorExchangeFallbacksAsync(CancellationToken stoppingToken)
    {
        var checkInterval = TimeSpan.FromSeconds(5);
        
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(checkInterval, stoppingToken);
                
                if (_eClientSocket?.IsConnected() != true)
                {
                    continue;
                }
                
                var now = DateTimeOffset.UtcNow;
                
                // Check each active subscription for L1 and tick-by-tick timeouts
                foreach (var kvp in _activeSubscriptions.ToList())
                {
                    var symbol = kvp.Key;
                    var sub = kvp.Value;
                    
                    // Check L1 receipt timeout
                    if (sub.MktDataRequestId.HasValue && 
                        sub.MktDataExchange != null && 
                        sub.MktDataExchange != "SMART" &&
                        sub.MktDataFirstReceiptMs.HasValue)
                    {
                        var ageMs = (now - sub.MktDataFirstReceiptMs.Value).TotalMilliseconds;
                        
                        // Check if we've received activity on this L1 subscription (via recordActivity callback)
                        var hasReceivedL1Data = _orderBooks.TryGetValue(symbol, out var book) && 
                                                book.RecentTrades.Count > 0;
                        
                        if (!hasReceivedL1Data && ageMs > _l1ReceiptTimeoutMs)
                        {
                            await TriggerL1Fallback(symbol, sub, ageMs);
                        }
                    }
                    
                    // Check tick-by-tick receipt timeout
                    if (sub.TickByTickRequestId.HasValue && 
                        sub.TickByTickExchange != null && 
                        sub.TickByTickExchange != "SMART" &&
                        sub.TickByTickFirstReceiptMs.HasValue)
                    {
                        var ageMs = (now - sub.TickByTickFirstReceiptMs.Value).TotalMilliseconds;
                        
                        // Check if we've received any tick-by-tick data (OrderBook would have recorded trades)
                        var hasReceivedTbtData = _orderBooks.TryGetValue(symbol, out var tbtBook) && 
                                                 tbtBook.RecentTrades.Count > 0;
                        
                        if (!hasReceivedTbtData && ageMs > _tickByTickReceiptTimeoutMs)
                        {
                            await TriggerTickByTickFallback(symbol, sub, ageMs);
                        }
                    }
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[IBKR] Error in fallback monitoring loop");
            }
        }
    }
    
    private async Task TriggerL1Fallback(string symbol, MarketDataSubscription sub, double ageMs)
    {
        if (_eClientSocket?.IsConnected() != true)
        {
            return;
        }
        
        _logger.LogInformation(
            "[IBKR] L1ExchangeFallback: symbol={Symbol} primaryExchange={PrimaryExchange} reason=NoDataReceived timeoutMs={TimeoutMs} ageMs={AgeMs} fallbackExchange=SMART",
            symbol,
            sub.MktDataExchange,
            _l1ReceiptTimeoutMs,
            (long)ageMs);
        
        // Cancel current L1 subscription
        if (sub.MktDataRequestId.HasValue)
        {
            try
            {
                _eClientSocket!.cancelMktData(sub.MktDataRequestId.Value);
            }
            catch { }
        }
        
        // Resubscribe with SMART
        try
        {
            var contract = new Contract
            {
                Symbol = symbol,
                SecType = "STK",
                Exchange = "SMART",
                Currency = "USD"
            };
            
            var newRequestId = _requestIdSource.NextId();
            _eClientSocket!.reqMktData(newRequestId, contract, string.Empty, false, false, null);
            _tickerIdMap[newRequestId] = symbol;
            
            // Update subscription with new request ID and SMART exchange
            var now = DateTimeOffset.UtcNow;
            var updated = sub with 
            { 
                MktDataRequestId = newRequestId,
                MktDataExchange = "SMART",
                MktDataFirstReceiptMs = now  // Reset timeout counter
            };
            _activeSubscriptions[symbol] = updated;
            
            _logger.LogDebug(
                "[IBKR] L1Resubscribed: symbol={Symbol} oldRequestId={OldId} newRequestId={NewId} exchange=SMART",
                symbol,
                sub.MktDataRequestId,
                newRequestId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[IBKR] Error during L1 fallback resubscription for {Symbol}", symbol);
        }
    }
    
    private async Task TriggerTickByTickFallback(string symbol, MarketDataSubscription sub, double ageMs)
    {
        if (_eClientSocket?.IsConnected() != true)
        {
            return;
        }
        
        _logger.LogInformation(
            "[IBKR] TickByTickExchangeFallback: symbol={Symbol} primaryExchange={PrimaryExchange} reason=NoDataReceived timeoutMs={TimeoutMs} ageMs={AgeMs} fallbackExchange=SMART",
            symbol,
            sub.TickByTickExchange,
            _tickByTickReceiptTimeoutMs,
            (long)ageMs);
        
        // Cancel current tick-by-tick subscription
        if (sub.TickByTickRequestId.HasValue)
        {
            try
            {
                _eClientSocket!.cancelTickByTickData(sub.TickByTickRequestId.Value);
            }
            catch { }
        }
        
        // Resubscribe with SMART
        try
        {
            var contract = new Contract
            {
                Symbol = symbol,
                SecType = "STK",
                Exchange = "SMART",
                Currency = "USD"
            };
            
            var newRequestId = _requestIdSource.NextId();
            _eClientSocket!.reqTickByTickData(newRequestId, contract, "Last", 0, false);
            _tickerIdMap[newRequestId] = symbol;
            
            // Update subscription with new request ID and SMART exchange
            var now = DateTimeOffset.UtcNow;
            var updated = sub with 
            { 
                TickByTickRequestId = newRequestId,
                TickByTickExchange = "SMART",
                TickByTickFirstReceiptMs = now  // Reset timeout counter
            };
            _activeSubscriptions[symbol] = updated;
            
            _logger.LogDebug(
                "[IBKR] TickByTickResubscribed: symbol={Symbol} oldRequestId={OldId} newRequestId={NewId} exchange=SMART",
                symbol,
                sub.TickByTickRequestId,
                newRequestId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[IBKR] Error during tick-by-tick fallback resubscription for {Symbol}", symbol);
        }
    }
    
    private void Cleanup()
    {
        try
        {
            if (_eClientSocket?.IsConnected() ?? false)
            {
                foreach (var subscription in _activeSubscriptions.Values)
                {
                    if (subscription.MktDataRequestId.HasValue)
                    {
                        _eClientSocket.cancelMktData(subscription.MktDataRequestId.Value);
                    }

                    if (subscription.DepthRequestId.HasValue)
                    {
                        _eClientSocket.cancelMktDepth(subscription.DepthRequestId.Value, false);
                    }

                    if (subscription.TickByTickRequestId.HasValue)
                    {
                        _eClientSocket.cancelTickByTickData(subscription.TickByTickRequestId.Value);
                    }
                }
                
                _eClientSocket.eDisconnect();
                _activeSubscriptions.Clear();
                _tickerIdMap.Clear();
                _orderBooks.Clear();
                _logger.LogInformation("[IBKR] Disconnected from TWS");
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[IBKR] Error during cleanup");
        }
    }

}

/// <summary>
/// Wrapper for IBApi callbacks
/// </summary>
internal class IBkrWrapperImpl : DefaultEWrapper
{
    private readonly ILogger<IBkrMarketDataClient> _logger;
    private readonly ConcurrentDictionary<int, string> _tickerIdMap;
    private readonly ConcurrentDictionary<string, OrderBookState> _orderBooks;
    private readonly OrderFlowMetrics _metrics;
    private readonly SignalCoordinator _signalCoordinator;
    private readonly PreviewSignalEmitter _previewSignalEmitter;
    private readonly Func<string, bool> _isTickByTickActive;
    private readonly Action<string>? _recordActivity;
    private readonly Action<string, long>? _recordTapeReceipt;
    private readonly Action<int, int, string>? _errorHandler;
    private readonly ConcurrentDictionary<string, byte> _depthEligibilityMarked = new(StringComparer.OrdinalIgnoreCase);
    private readonly Action<string>? _markDepthEligible;
    private readonly Action<int>? _recordDepthUpdate;
    private readonly ConcurrentDictionary<int, LastTradeState> _lastTrades = new();
    
    public IBkrWrapperImpl(
        ILogger<IBkrMarketDataClient> logger,
        ConcurrentDictionary<int, string> tickerIdMap,
        ConcurrentDictionary<string, OrderBookState> orderBooks,
        OrderFlowMetrics metrics,
        SignalCoordinator signalCoordinator,
        PreviewSignalEmitter previewSignalEmitter,
        Func<string, bool> isTickByTickActive,
        Action<string>? recordActivity,
        Action<string, long>? recordTapeReceipt,
        Action<int, int, string>? errorHandler,
        Action<string>? markDepthEligible,
        Action<int>? recordDepthUpdate)
    {
        _logger = logger;
        _tickerIdMap = tickerIdMap;
        _orderBooks = orderBooks;
        _metrics = metrics;
        _signalCoordinator = signalCoordinator;
        _previewSignalEmitter = previewSignalEmitter;
        _isTickByTickActive = isTickByTickActive;
        _recordActivity = recordActivity;
        _recordTapeReceipt = recordTapeReceipt;
        _errorHandler = errorHandler;
        _markDepthEligible = markDepthEligible;
        _recordDepthUpdate = recordDepthUpdate;
    }

    private bool TryGetBook(int tickerId, out OrderBookState book)
    {
        book = default!;

        if (!_tickerIdMap.TryGetValue(tickerId, out var symbol))
        {
            return false;
        }

        if (!_orderBooks.TryGetValue(symbol, out var existing))
        {
            existing = new OrderBookState { Symbol = symbol };
            _orderBooks[symbol] = existing;
        }

        book = existing;
        _metrics.RegisterOrderBook(book);
        return true;
    }

    // === Core callbacks we care about (Phase 1-2) ===
    public override void updateMktDepth(int tickerId, int position, int operation, int side, double price, decimal size)
    {
        try
        {
            if (!TryGetBook(tickerId, out var book))
            {
                return;
            }

            _recordDepthUpdate?.Invoke(tickerId);

            var nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            var px = (decimal)price;
            var sz = size;
            var depthSide = side == 0 ? DepthSide.Ask : DepthSide.Bid;

            // side: 0=ask, 1=bid (per IB API convention)
            // operation: 0=insert, 1=update, 2=delete
            if (!Enum.IsDefined(typeof(DepthOperation), operation))
            {
                _logger.LogWarning("[IBKR Depth] Invalid operation {Operation} for tickerId={TickerId}", operation, tickerId);
                return;
            }

            var depthUpdate = new DepthUpdate(
                book.Symbol,
                depthSide,
                (DepthOperation)operation,
                px,
                sz,
                position,
                nowMs);
            book.LastDepthRecvMs = nowMs;  // Track depth receipt time for diagnostics
            book.ApplyDepthUpdate(depthUpdate);
            MarkDepthEligibleOnce(book.Symbol);

            // Fix 3: Only update metrics if book is valid
            if (book.IsBookValid(out var validityReason, nowMs))
            {
                _metrics.UpdateMetrics(book, nowMs);
                _signalCoordinator.ProcessSnapshot(book, nowMs);
                _ = _previewSignalEmitter.ProcessSnapshotAsync(book, nowMs);
            }
            else
            {
                _logger.LogDebug("[IBKR Depth] Skipped metrics for {Symbol}: {Reason}", book.Symbol, validityReason);
            }
        }
        catch (Exception ex)
        {
            // Fix 4: Log exception and return safely, do not propagate
            _logger.LogError(ex, "[IBKR Depth] Error processing updateMktDepth callback for tickerId={TickerId}", tickerId);
        }
    }

    public override void updateMktDepthL2(int tickerId, int position, string marketMaker, int operation, int side, double price, decimal size, bool isSmartDepth)
    {
        try
        {
            if (!TryGetBook(tickerId, out var book))
            {
                return;
            }

            _recordDepthUpdate?.Invoke(tickerId);

            if (!Enum.IsDefined(typeof(DepthOperation), operation))
            {
                _logger.LogWarning("[IBKR DepthL2] Invalid operation {Operation} for tickerId={TickerId}", operation, tickerId);
                return;
            }

            var nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            var depthSide = side == 0 ? DepthSide.Ask : DepthSide.Bid;
            var depthUpdate = new DepthUpdate(
                book.Symbol,
                depthSide,
                (DepthOperation)operation,
                (decimal)price,
                size,
                position,
                nowMs);
            book.LastDepthRecvMs = nowMs;  // Track depth receipt time for diagnostics
            book.ApplyDepthUpdate(depthUpdate);
            MarkDepthEligibleOnce(book.Symbol);
            _markDepthEligible?.Invoke(book.Symbol);

            if (book.IsBookValid(out var validityReason, nowMs))
            {
                _metrics.UpdateMetrics(book, nowMs);
                _signalCoordinator.ProcessSnapshot(book, nowMs);
                _ = _previewSignalEmitter.ProcessSnapshotAsync(book, nowMs);
            }
            else
            {
                _logger.LogDebug("[IBKR DepthL2] Skipped metrics for {Symbol}: {Reason}", book.Symbol, validityReason);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[IBKR DepthL2] Error processing updateMktDepthL2 callback for tickerId={TickerId}", tickerId);
        }
    }

    public override void tickByTickAllLast(int reqId, int tickType, long time, double price, decimal size, TickAttribLast tickAttribLast, string exchange, string specialConditions)
    {
        try
        {
            if (!TryGetBook(reqId, out var book))
            {
                return;
            }

            // Capture receipt time immediately
            var recvMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            
            // IB gives epoch seconds for some callbacks; normalize to ms when it looks like seconds.
            var eventMs = time < 10_000_000_000 ? time * 1000 : time;
            
            // Record with both timestamps: event time (IB server) and receipt time (local)
            book.RecordTrade(eventMs, recvMs, price, size);
            _recordActivity?.Invoke(book.Symbol);
            _recordTapeReceipt?.Invoke(book.Symbol, recvMs);

            // Fix 3: Only update metrics if book is valid
            if (book.IsBookValid(out var validityReason, recvMs))
            {
                _metrics.UpdateMetrics(book, recvMs);
                _signalCoordinator.ProcessSnapshot(book, recvMs);
                _ = _previewSignalEmitter.ProcessSnapshotAsync(book, recvMs);
            }
            else
            {
                _logger.LogDebug("[IBKR Tape] Skipped metrics for {Symbol}: {Reason}", book.Symbol, validityReason);
            }
        }
        catch (Exception ex)
        {
            // Fix 4: Log exception and return safely, do not propagate
            _logger.LogError(ex, "[IBKR Tape] Error processing tickByTickAllLast callback for reqId={ReqId}", reqId);
        }
    }

    private void MarkDepthEligibleOnce(string symbol)
    {
        if (_depthEligibilityMarked.TryAdd(symbol, 0))
        {
            _markDepthEligible?.Invoke(symbol);
        }
    }

    private void TryRecordMktDataTrade(OrderBookState book, LastTradeState state)
    {
        if (!state.LastPrice.HasValue || !state.LastSize.HasValue)
        {
            return;
        }

        var price = (double)state.LastPrice.Value;
        var size = state.LastSize.Value;
        if (price <= 0 || size <= 0)
        {
            return;
        }

        var nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        
        // Track L1 receipt time for triage eligibility
        book.LastL1RecvMs = nowMs;
        
        // Market data callbacks don't provide event time, so use receipt time for both
        book.RecordTrade(nowMs, nowMs, price, size);
        _recordActivity?.Invoke(book.Symbol);
        _recordTapeReceipt?.Invoke(book.Symbol, nowMs);

        if (book.IsBookValid(out var validityReason, nowMs))
        {
            _metrics.UpdateMetrics(book, nowMs);
            _signalCoordinator.ProcessSnapshot(book, nowMs);
            _ = _previewSignalEmitter.ProcessSnapshotAsync(book, nowMs);
        }
        else
        {
            _logger.LogDebug("[IBKR Tape] Skipped metrics for {Symbol}: {Reason}", book.Symbol, validityReason);
        }

        state.LastSize = null;
    }

    public override void tickByTickBidAsk(int reqId, long time, double bidPrice, double askPrice, decimal bidSize, decimal askSize, TickAttribBidAsk tickAttribBidAsk)
    {
        // Not used in Phase 1 prompt (we subscribe to AllLast). Intentionally no-op.
    }

    public override void tickByTickMidPoint(int reqId, long time, double midPoint)
    {
        // Not used.
    }

    public override void connectionClosed() => _logger.LogWarning("[IBKR] Connection closed");

    public override void error(int id, long errorTime, int errorCode, string errorMsg, string advancedOrderRejectJson)
    {
        if (errorCode == 2104 || errorCode == 2106 || errorCode == 2158 || errorCode == 2176)
        {
            _logger.LogDebug("[IBKR Info {ErrorCode}] ID={Id}: {Message}", errorCode, id, errorMsg);
            return;
        }
        
        // Log tick-by-tick errors prominently
        if (errorCode == 10167 || errorCode == 10168 || errorCode == 321 || errorMsg.Contains("tick-by-tick", StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogWarning("[IBKR TickByTick Error {ErrorCode}] ID={Id}: {Message}", errorCode, id, errorMsg);
        }
        
        if (id > 0)
        {
            _errorHandler?.Invoke(id, errorCode, errorMsg);
        }
        
        if (id > 0)
        {
            _logger.LogInformation("[IBKR Error {ErrorCode}] ID={Id}: {Message}", errorCode, id, errorMsg);
        }
        else
        {
            _logger.LogDebug("[IBKR Error {ErrorCode}] ID={Id}: {Message}", errorCode, id, errorMsg);
        }
    }

    public override void error(string str) => _logger.LogError("[IBKR Error] {Message}", str);

    public override void error(Exception e) => _logger.LogError(e, "[IBKR Exception]");

    public override void currentTime(long time) { }

    // === Remaining EWrapper members (stubs) ===
    public override void tickPrice(int tickerId, int field, double price, TickAttrib attribs)
    {
        if (field != TickType.BID && field != TickType.ASK && field != TickType.LAST && 
            field != TickType.DELAYED_BID && field != TickType.DELAYED_ASK && field != TickType.DELAYED_LAST)
        {
            return;
        }

        if (!TryGetBook(tickerId, out var book))
        {
            return;
        }

        // Track L1 receipt time for triage eligibility
        var nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        book.LastL1RecvMs = nowMs;
        _recordActivity?.Invoke(book.Symbol);

        // Skip L1 updates if TBT or Level 2 depth is active to avoid jitter
        if (_isTickByTickActive(book.Symbol) || book.MaxDepthRows > 1) return;

        switch (field)
        {
            case TickType.BID:
            case TickType.DELAYED_BID:
                book.UpdateBidDepth((decimal)price, book.BidLevels.Count > 0 ? book.BidLevels[0].Size : 0m, nowMs, 0);
                break;
            case TickType.ASK:
            case TickType.DELAYED_ASK:
                book.UpdateAskDepth((decimal)price, book.AskLevels.Count > 0 ? book.AskLevels[0].Size : 0m, nowMs, 0);
                break;
            case TickType.LAST:
            case TickType.DELAYED_LAST:
                if (price <= 0) return;
                var tradeState = _lastTrades.GetOrAdd(tickerId, _ => new LastTradeState());
                tradeState.LastPrice = (decimal)price;
                TryRecordMktDataTrade(book, tradeState);
                break;
        }
    }

    public override void tickSize(int tickerId, int field, decimal size)
    {
        if (field != TickType.BID_SIZE && field != TickType.ASK_SIZE && field != TickType.LAST_SIZE && 
            field != TickType.DELAYED_BID_SIZE && field != TickType.DELAYED_ASK_SIZE && field != TickType.DELAYED_LAST_SIZE)
        {
            return;
        }

        if (!TryGetBook(tickerId, out var book))
        {
            return;
        }

        // Track L1 receipt time for triage eligibility
        var nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        book.LastL1RecvMs = nowMs;
        _recordActivity?.Invoke(book.Symbol);

        // Skip L1 updates if TBT or Level 2 depth is active to avoid jitter
        if (_isTickByTickActive(book.Symbol) || book.MaxDepthRows > 1) return;

        switch (field)
        {
            case TickType.BID_SIZE:
            case TickType.DELAYED_BID_SIZE:
                book.UpdateBidDepth(book.BidLevels.Count > 0 ? book.BidLevels[0].Price : 0m, size, nowMs, 0);
                break;
            case TickType.ASK_SIZE:
            case TickType.DELAYED_ASK_SIZE:
                book.UpdateAskDepth(book.AskLevels.Count > 0 ? book.AskLevels[0].Price : 0m, size, nowMs, 0);
                break;
            case TickType.LAST_SIZE:
            case TickType.DELAYED_LAST_SIZE:
                if (size <= 0) return;
                var tradeState = _lastTrades.GetOrAdd(tickerId, _ => new LastTradeState());
                tradeState.LastSize = size;
                TryRecordMktDataTrade(book, tradeState);
                break;
        }
    }
    private sealed class LastTradeState
    {
        public decimal? LastPrice { get; set; }
        public decimal? LastSize { get; set; }
    }

}
