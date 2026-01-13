using IBApi;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Configuration;
using RamStockAlerts.Engine;
using RamStockAlerts.Models;
using RamStockAlerts.Services;
using RamStockAlerts.Universe;
using System.Collections.Concurrent;
using RamStockAlerts.Services.Universe;

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
    private readonly ShadowTradingCoordinator _shadowTradingCoordinator;
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
    private int _nextRequestId = 1000;
    
    public IBkrMarketDataClient(
        ILogger<IBkrMarketDataClient> logger,
        IConfiguration configuration,
        UniverseService universeService,
        MarketDataSubscriptionManager subscriptionManager,
        OrderFlowMetrics metrics,
        ShadowTradingCoordinator shadowTradingCoordinator,
        PreviewSignalEmitter previewSignalEmitter,
        ContractClassificationService classificationService,
        DepthEligibilityCache depthEligibilityCache)
    {
        _logger = logger;
        _configuration = configuration;
        _universeService = universeService;
        _subscriptionManager = subscriptionManager;
        _metrics = metrics;
        _shadowTradingCoordinator = shadowTradingCoordinator;
        _previewSignalEmitter = previewSignalEmitter;
        _classificationService = classificationService;
        _depthEligibilityCache = depthEligibilityCache;
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
                _shadowTradingCoordinator,
                _previewSignalEmitter,
                IsTickByTickActive,
                _subscriptionManager.RecordActivity,
                HandleIbkrError,
                MarkDepthEligible);
            _readerSignal = new EReaderMonitorSignal();
            _eClientSocket = new EClientSocket(_wrapper, _readerSignal);
            
            // Connect to TWS
            var host = _configuration["IBKR:Host"] ?? _configuration["Ibkr:Host"] ?? "127.0.0.1";
            var port = _configuration.GetValue<int?>("IBKR:Port")
                       ?? _configuration.GetValue<int?>("Ibkr:Port")
                       ?? 7497; // default to paper trading port
            var clientId = _configuration.GetValue<int?>("IBKR:ClientId")
                          ?? _configuration.GetValue<int?>("Ibkr:ClientId")
                          ?? 1;

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
            
            _logger.LogInformation("[IBKR] Market data client ready. Managing subscriptions...");

            var refreshInterval = TimeSpan.FromMinutes(1);
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
        if (_activeSubscriptions.TryGetValue(normalized, out var existing))
        {
            return existing;
        }

        var enableTape = _configuration.GetValue("MarketData:EnableTape", true);
        var enableDepth = _configuration.GetValue("MarketData:EnableDepth", true);
        requestDepth = requestDepth && enableDepth;
        if (!enableTape && !requestDepth)
        {
            _logger.LogWarning("[IBKR] Subscribe skipped for {Symbol}: depth/tape disabled.", normalized);
            return null;
        }

        var classification = _classificationService.TryGetCached(normalized);
        if (requestDepth && !_depthEligibilityCache.CanRequestDepth(classification, normalized, DateTimeOffset.UtcNow, out var eligibilityState))
        {
            _depthEligibilityCache.LogSkipOnce(classification, normalized, eligibilityState);
            requestDepth = false;
        }

        var depthAttempted = false;
        await _subscriptionLock.WaitAsync(cancellationToken);
        int? mktDataRequestId = null;
        int? depthRequestId = null;
        try
        {
            if (_activeSubscriptions.TryGetValue(normalized, out existing))
            {
                return existing;
            }

            var contract = new Contract
            {
                Symbol = normalized,
                SecType = "STK",
                Exchange = "SMART",
                Currency = "USD"
            };

            if (enableTape)
            {
                mktDataRequestId = Interlocked.Increment(ref _nextRequestId);
                _eClientSocket.reqMktData(mktDataRequestId.Value, contract, string.Empty, false, false, null);
                _tickerIdMap[mktDataRequestId.Value] = normalized;
            }

            if (requestDepth)
            {
                _subscriptionManager.RecordDepthSubscribeAttempt(normalized);
                depthAttempted = true;
                var depthRows = Math.Clamp(_configuration.GetValue("MarketData:DepthRows", 5), 1, 10);
                depthRequestId = Interlocked.Increment(ref _nextRequestId);
                _eClientSocket.reqMarketDepth(depthRequestId.Value, contract, depthRows, false, null);
                _tickerIdMap[depthRequestId.Value] = normalized;
                _subscriptionManager.RecordDepthSubscribeSuccess(normalized, depthRequestId.Value);
            }

            if (mktDataRequestId is null && depthRequestId is null)
            {
                return null;
            }

            _orderBooks.AddOrUpdate(
                normalized,
                _ => new OrderBookState { Symbol = normalized },
                (_, existingBook) => existingBook);

            var subscription = new MarketDataSubscription(normalized, mktDataRequestId, depthRequestId, null);
            _activeSubscriptions[normalized] = subscription;

            _logger.LogInformation(
                "[IBKR] Subscribed to {Symbol} mktDataId={MktDataId} depthId={DepthId}",
                normalized,
                mktDataRequestId,
                depthRequestId);

            return subscription;
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

            var contract = new Contract
            {
                Symbol = normalized,
                SecType = "STK",
                Exchange = "SMART",
                Currency = "USD"
            };

            requestId = Interlocked.Increment(ref _nextRequestId);
            _eClientSocket.reqTickByTickData(requestId.Value, contract, "Last", 0, false);
            _tickerIdMap[requestId.Value] = normalized;

            var updated = existing with { TickByTickRequestId = requestId.Value };
            _activeSubscriptions[normalized] = updated;

            _logger.LogInformation("[IBKR] Enabled tick-by-tick for {Symbol} tickByTickId={TickByTickId}", normalized, requestId);
            return requestId;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[IBKR] Error enabling tick-by-tick for {Symbol}", normalized);
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

            _logger.LogDebug(
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
        if (errorCode != 10092 && errorCode != 10190)
        {
            return;
        }

        _ = _subscriptionManager.HandleIbkrErrorAsync(
            requestId,
            errorCode,
            errorMessage,
            DisableDepthAsync,
            DisableTickByTickAsync,
            CancellationToken.None);
    }

    private void MarkDepthEligible(string symbol)
    {
        var classification = _classificationService.TryGetCached(symbol);
        _depthEligibilityCache.MarkEligible(classification, symbol);
    }
    
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
internal class IBkrWrapperImpl : EWrapper
{
    private readonly ILogger<IBkrMarketDataClient> _logger;
    private readonly ConcurrentDictionary<int, string> _tickerIdMap;
    private readonly ConcurrentDictionary<string, OrderBookState> _orderBooks;
    private readonly OrderFlowMetrics _metrics;
    private readonly ShadowTradingCoordinator _shadowTradingCoordinator;
    private readonly PreviewSignalEmitter _previewSignalEmitter;
    private readonly Func<string, bool> _isTickByTickActive;
    private readonly Action<string>? _recordActivity;
    private readonly Action<int, int, string>? _errorHandler;
    private readonly ConcurrentDictionary<string, byte> _depthEligibilityMarked = new(StringComparer.OrdinalIgnoreCase);
    private readonly Action<string>? _markDepthEligible;
    private readonly ConcurrentDictionary<int, LastTradeState> _lastTrades = new();
    
    public IBkrWrapperImpl(
        ILogger<IBkrMarketDataClient> logger,
        ConcurrentDictionary<int, string> tickerIdMap,
        ConcurrentDictionary<string, OrderBookState> orderBooks,
        OrderFlowMetrics metrics,
        ShadowTradingCoordinator shadowTradingCoordinator,
        PreviewSignalEmitter previewSignalEmitter,
        Func<string, bool> isTickByTickActive,
        Action<string>? recordActivity,
        Action<int, int, string>? errorHandler,
        Action<string>? markDepthEligible)
    {
        _logger = logger;
        _tickerIdMap = tickerIdMap;
        _orderBooks = orderBooks;
        _metrics = metrics;
        _shadowTradingCoordinator = shadowTradingCoordinator;
        _previewSignalEmitter = previewSignalEmitter;
        _isTickByTickActive = isTickByTickActive;
        _recordActivity = recordActivity;
        _errorHandler = errorHandler;
        _markDepthEligible = markDepthEligible;
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
        return true;
    }

    // === Core callbacks we care about (Phase 1-2) ===
    public void updateMktDepth(int tickerId, int position, int operation, int side, double price, int size)
    {
        try
        {
            if (!TryGetBook(tickerId, out var book))
            {
                return;
            }

            var nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            var px = (decimal)price;
            var sz = (decimal)size;
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
            book.ApplyDepthUpdate(depthUpdate);
            MarkDepthEligibleOnce(book.Symbol);

            // Fix 3: Only update metrics if book is valid
            if (book.IsBookValid(out var validityReason, nowMs))
            {
                _metrics.UpdateMetrics(book, nowMs);
                _shadowTradingCoordinator.ProcessSnapshot(book, nowMs);
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

    public void updateMktDepthL2(int tickerId, int position, string marketMaker, int operation, int side, double price, int size, bool isSmartDepth)
    {
        try
        {
            if (!TryGetBook(tickerId, out var book))
            {
                return;
            }

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
                (decimal)size,
                position,
                nowMs);
            book.ApplyDepthUpdate(depthUpdate);
            MarkDepthEligibleOnce(book.Symbol);
            _markDepthEligible?.Invoke(book.Symbol);

            if (book.IsBookValid(out var validityReason, nowMs))
            {
                _metrics.UpdateMetrics(book, nowMs);
                _shadowTradingCoordinator.ProcessSnapshot(book, nowMs);
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

    public void tickByTickAllLast(int reqId, int tickType, long time, double price, int size, TickAttribLast tickAttribLast, string exchange, string specialConditions)
    {
        try
        {
            if (!TryGetBook(reqId, out var book))
            {
                return;
            }

            // IB gives epoch seconds for some callbacks; normalize to ms when it looks like seconds.
            var tsMs = time < 10_000_000_000 ? time * 1000 : time;
            book.RecordTrade(tsMs, price, (decimal)size);
            _recordActivity?.Invoke(book.Symbol);

            // Fix 3: Only update metrics if book is valid
            if (book.IsBookValid(out var validityReason, tsMs))
            {
                _metrics.UpdateMetrics(book, tsMs);
                _shadowTradingCoordinator.ProcessSnapshot(book, tsMs);
                _ = _previewSignalEmitter.ProcessSnapshotAsync(book, tsMs);
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
        book.RecordTrade(nowMs, price, size);
        _recordActivity?.Invoke(book.Symbol);

        if (book.IsBookValid(out var validityReason, nowMs))
        {
            _metrics.UpdateMetrics(book, nowMs);
            _shadowTradingCoordinator.ProcessSnapshot(book, nowMs);
            _ = _previewSignalEmitter.ProcessSnapshotAsync(book, nowMs);
        }
        else
        {
            _logger.LogDebug("[IBKR Tape] Skipped metrics for {Symbol}: {Reason}", book.Symbol, validityReason);
        }

        state.LastSize = null;
    }

    public void tickByTickBidAsk(int reqId, long time, double bidPrice, double askPrice, int bidSize, int askSize, TickAttribBidAsk tickAttribBidAsk)
    {
        // Not used in Phase 1 prompt (we subscribe to AllLast). Intentionally no-op.
    }

    public void tickByTickMidPoint(int reqId, long time, double midPoint)
    {
        // Not used.
    }

    public void connectionClosed() => _logger.LogWarning("[IBKR] Connection closed");

    public void error(int id, int errorCode, string errorMsg)
    {
        if (errorCode == 2104 || errorCode == 2106 || errorCode == 2158)
        {
            _logger.LogDebug("[IBKR Info {ErrorCode}] ID={Id}: {Message}", errorCode, id, errorMsg);
            return;
        }

        if (errorCode == 10092 || errorCode == 10190)
        {
            _errorHandler?.Invoke(id, errorCode, errorMsg);
            return;
        }

        _logger.LogError("[IBKR Error {ErrorCode}] ID={Id}: {Message}", errorCode, id, errorMsg);
    }

    public void error(string str) => _logger.LogError("[IBKR Error] {Message}", str);

    public void error(Exception e) => _logger.LogError(e, "[IBKR Exception]");

    public void currentTime(long time) { }

    // === Remaining EWrapper members (stubs) ===
    public void tickPrice(int tickerId, int field, double price, TickAttrib attribs)
    {
        if (field != TickType.LAST && field != TickType.DELAYED_LAST)
        {
            return;
        }

        if (!TryGetBook(tickerId, out var book))
        {
            return;
        }

        if (_isTickByTickActive(book.Symbol))
        {
            return;
        }

        if (price <= 0)
        {
            return;
        }

        var state = _lastTrades.GetOrAdd(tickerId, _ => new LastTradeState());
        state.LastPrice = (decimal)price;
        TryRecordMktDataTrade(book, state);
    }

    public void tickSize(int tickerId, int field, int size)
    {
        if (field != TickType.LAST_SIZE && field != TickType.DELAYED_LAST_SIZE)
        {
            return;
        }

        if (!TryGetBook(tickerId, out var book))
        {
            return;
        }

        if (_isTickByTickActive(book.Symbol))
        {
            return;
        }

        if (size <= 0)
        {
            return;
        }

        var state = _lastTrades.GetOrAdd(tickerId, _ => new LastTradeState());
        state.LastSize = size;
        TryRecordMktDataTrade(book, state);
    }
    public void tickOptionComputation(int tickerId, int field, double impliedVol, double delta, double optPrice, double pvDividend, double gamma, double vega, double theta, double undPrice) { }
    public void tickGeneric(int tickerId, int tickType, double value) { }
    public void tickString(int tickerId, int tickType, string value) { }
    public void tickEFP(int tickerId, int tickType, double basisPoints, string formattedBasisPoints, double impliedFuture, int holdDays, string futureExpiry, double dividendImpact, double dividendsToExpiry) { }
    public void tickSnapshotEnd(int tickerId) { }
    public void nextValidId(int orderId) { }
    public void deltaNeutralValidation(int reqId, DeltaNeutralContract deltaNeutralContract) { }

    public void orderStatus(int orderId, string status, double filled, double remaining, double avgFillPrice, int permId, int parentId, double lastFillPrice, int clientId, string whyHeld, double mktCapPrice) { }
    public void openOrder(int orderId, Contract contract, Order order, OrderState orderState) { }
    public void openOrderEnd() { }
    public void updateAccountValue(string key, string value, string currency, string accountName) { }
    public void updatePortfolio(Contract contract, double position, double marketPrice, double marketValue, double averageCost, double unrealizedPNL, double realizedPNL, string accountName) { }
    public void updateAccountTime(string timeStamp) { }
    public void accountDownloadEnd(string accountName) { }
    public void accountSummary(int reqId, string account, string tag, string value, string currency) { }
    public void accountSummaryEnd(int reqId) { }

    public void contractDetails(int reqId, ContractDetails contractDetails) { }
    public void bondContractDetails(int reqId, ContractDetails contractDetails) { }
    public void contractDetailsEnd(int reqId) { }

    public void execDetails(int reqId, Contract contract, Execution execution) { }
    public void execDetailsEnd(int reqId) { }
    public void commissionReport(CommissionReport commissionReport) { }
    public void fundamentalData(int reqId, string data) { }
    public void marketDataType(int reqId, int marketDataType) { }
    public void updateNewsBulletin(int newsMsgId, int newsMsgType, string newsMessage, string originExch) { }
    public void managedAccounts(string accountsList) { }
    public void receiveFA(int faDataType, string faXmlData) { }
    public void historicalData(int reqId, Bar bar) { }
    public void historicalDataEnd(int reqId, string startDateStr, string endDateStr) { }
    public void historicalDataUpdate(int reqId, Bar bar) { }
    public void historicalTicks(int reqId, HistoricalTick[] ticks, bool done) { }
    public void historicalTicksBidAsk(int reqId, HistoricalTickBidAsk[] ticks, bool done) { }
    public void historicalTicksLast(int reqId, HistoricalTickLast[] ticks, bool done) { }
    public void headTimestamp(int reqId, string headTimestamp) { }

    public void rerouteMktDataReq(int reqId, int conId, string exchange) { }
    public void rerouteMktDepthReq(int reqId, int conId, string exchange) { }
    public void marketRule(int marketRuleId, PriceIncrement[] priceIncrements) { }

    public void position(string account, Contract contract, double pos, double avgCost) { }
    public void positionEnd() { }
    public void positionMulti(int reqId, string account, string modelCode, Contract contract, double pos, double avgCost) { }
    public void positionMultiEnd(int reqId) { }
    public void accountUpdateMulti(int reqId, string account, string modelCode, string key, string value, string currency) { }
    public void accountUpdateMultiEnd(int reqId) { }

    public void realtimeBar(int reqId, long time, double open, double high, double low, double close, long volume, double wap, int count) { }
    public void scannerParameters(string xml) { }
    public void scannerData(int reqId, int rank, ContractDetails contractDetails, string distance, string benchmark, string projection, string legsStr) { }
    public void scannerDataEnd(int reqId) { }

    public void verifyMessageAPI(string apiData) { }
    public void verifyCompleted(bool isSuccessful, string errorText) { }
    public void verifyAndAuthMessageAPI(string apiData, string xyzChallange) { }
    public void verifyAndAuthCompleted(bool isSuccessful, string errorText) { }
    public void connectAck() { }

    public void securityDefinitionOptionParameter(int reqId, string exchange, int underlyingConId, string tradingClass, string multiplier, HashSet<string> expirations, HashSet<double> strikes) { }
    public void securityDefinitionOptionParameterEnd(int reqId) { }
    public void softDollarTiers(int reqId, SoftDollarTier[] tiers) { }
    public void familyCodes(FamilyCode[] familyCodes) { }
    public void symbolSamples(int reqId, ContractDescription[] contractDescriptions) { }

    public void tickNews(int tickerId, long timeStamp, string providerCode, string articleId, string headline, string extraData) { }
    public void smartComponents(int reqId, Dictionary<int, KeyValuePair<string, char>> theMap) { }
    public void newsProviders(NewsProvider[] newsProviders) { }
    public void newsArticle(int requestId, int articleType, string articleText) { }
    public void historicalNews(int requestId, string time, string providerCode, string articleId, string headline) { }
    public void historicalNewsEnd(int requestId, bool hasMore) { }
    public void histogramData(int reqId, HistogramEntry[] data) { }
    public void pnl(int reqId, double dailyPnL, double unrealizedPnL, double realizedPnL) { }
    public void pnlSingle(int reqId, int pos, double dailyPnL, double unrealizedPnL, double realizedPnL, double value) { }

    public void mktDepthExchanges(DepthMktDataDescription[] depthMktDataDescriptions) { }
    public void wshMetaData(int reqId, string dataJson) { }
    public void wshEventData(int reqId, string dataJson) { }

    public void orderBound(long orderId, int apiClientId, int apiOrderId) { }
    public void completedOrder(Contract contract, Order order, OrderState orderState) { }
    public void completedOrdersEnd() { }

    public void replaceFAEnd(int reqId, string text) { }
    public void displayGroupList(int reqId, string groups) { }
    public void displayGroupUpdated(int reqId, string contractInfo) { }

    // Added in newer APIs; keep stubs for compatibility if present
    public void tickReqParams(int tickerId, double minTick, string bboExchange, int snapshotPermissions) { }
    public void tickByTick(int reqId, int tickType, long time, double price, int size, TickAttribLast tickAttribLast, string exchange, string specialConditions) { }

    private sealed class LastTradeState
    {
        public decimal? LastPrice { get; set; }
        public decimal? LastSize { get; set; }
    }

}
