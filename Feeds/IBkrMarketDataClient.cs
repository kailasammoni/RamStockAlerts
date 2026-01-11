using IBApi;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Configuration;
using RamStockAlerts.Engine;
using RamStockAlerts.Models;
using System.Collections.Concurrent;

namespace RamStockAlerts.Feeds;

/// <summary>
/// Interactive Brokers Level-II Market Data Client
/// 
/// Subscribes to:
/// - Market Depth (L2 order book snapshot, 10 levels)
/// - Tick-by-Tick Data (real-time tape: ALL_LAST trades)
/// 
/// Purpose: Feed OrderFlowMetrics with real order-book imbalances and tape acceleration
/// </summary>
public class IBkrMarketDataClient : BackgroundService
{
    private readonly ILogger<IBkrMarketDataClient> _logger;
    private readonly IConfiguration _configuration;
    private readonly UniverseBuilder _universeBuilder;
    private readonly OrderFlowMetrics _metrics;
    
    private EClientSocket? _eClientSocket;
    private EReaderSignal? _readerSignal;
    private IBkrWrapperImpl? _wrapper;
    
    private readonly ConcurrentDictionary<int, string> _tickerIdMap = new();
    private readonly ConcurrentDictionary<string, OrderBookState> _orderBooks = new();
    
    private const int MARKET_DEPTH_LEVELS = 10;
    
    public IBkrMarketDataClient(
        ILogger<IBkrMarketDataClient> logger,
        IConfiguration configuration,
        UniverseBuilder universeBuilder,
        OrderFlowMetrics metrics)
    {
        _logger = logger;
        _configuration = configuration;
        _universeBuilder = universeBuilder;
        _metrics = metrics;
    }
    
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            _logger.LogInformation("[IBKR] Initializing market data client...");
            
            // Create socket and wrapper
            _wrapper = new IBkrWrapperImpl(_logger, _tickerIdMap, _orderBooks, _metrics);
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
            
            // Wait for universe to load
            await Task.Delay(2000, stoppingToken);
            
            // Get tickers from universe
            var tickers = await _universeBuilder.GetActiveUniverseAsync(stoppingToken);
            while (!stoppingToken.IsCancellationRequested && tickers.Count == 0)
            {
                _logger.LogWarning("[IBKR] No tickers in universe. Waiting...");
                await Task.Delay(5000, stoppingToken);
                tickers = await _universeBuilder.GetActiveUniverseAsync(stoppingToken);
            }

            var maxSymbols = _configuration.GetValue("IBKR:MaxSymbols", 20);
            
            // Subscribe to market depth and tick-by-tick for each ticker
            var tickerId = 1;
            foreach (var ticker in tickers.Take(maxSymbols))
            {
                try
                {
                    // Create contract
                    var contract = new Contract
                    {
                        Symbol = ticker,
                        SecType = "STK",
                        Exchange = "SMART",
                        Currency = "USD"
                    };
                    
                    // Subscribe to Level 2 depth (10 levels)
                    _eClientSocket.reqMarketDepth(tickerId, contract, MARKET_DEPTH_LEVELS, false, null);
                    
                    // Subscribe to tick-by-tick data (ALL_LAST = all trades)
                    _eClientSocket.reqTickByTickData(tickerId, contract, "AllLast", 0, false);
                    
                    _tickerIdMap[tickerId] = ticker;
                    _orderBooks[ticker] = new OrderBookState { Symbol = ticker };
                    
                    _logger.LogInformation("[IBKR] Subscribed to {Ticker} (ID: {TickerId})", ticker, tickerId);
                    
                    tickerId++;
                    await Task.Delay(100, stoppingToken); // Stagger requests
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "[IBKR] Error subscribing to {Ticker}", ticker);
                }
            }
            
            _logger.LogInformation("[IBKR] Market data subscriptions active. Processing events...");
            
            // Keep alive until cancellation
            await Task.Delay(Timeout.Infinite, stoppingToken);
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
                // Unsubscribe from all symbols
                foreach (var tickerId in _tickerIdMap.Keys)
                {
                    _eClientSocket.cancelMktDepth(tickerId, false);
                    _eClientSocket.cancelTickByTickData(tickerId);
                }
                
                _eClientSocket.eDisconnect();
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
    
    public IBkrWrapperImpl(
        ILogger<IBkrMarketDataClient> logger,
        ConcurrentDictionary<int, string> tickerIdMap,
        ConcurrentDictionary<string, OrderBookState> orderBooks,
        OrderFlowMetrics metrics)
    {
        _logger = logger;
        _tickerIdMap = tickerIdMap;
        _orderBooks = orderBooks;
        _metrics = metrics;
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

            // side: 0=ask, 1=bid (per IB API convention)
            // operation: 0=insert, 1=update, 2=delete
            if (operation == 2 || size <= 0)
            {
                if (side == 0)
                {
                    book.UpdateAskDepth(px, 0m, nowMs);
                }
                else
                {
                    book.UpdateBidDepth(px, 0m, nowMs);
                }
            }
            else
            {
                if (side == 0)
                {
                    book.UpdateAskDepth(px, sz, nowMs);
                }
                else
                {
                    book.UpdateBidDepth(px, sz, nowMs);
                }
            }

            // Fix 3: Only update metrics if book is valid
            if (book.IsBookValid(out var validityReason, nowMs))
            {
                _metrics.UpdateMetrics(book, nowMs);
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
        // We currently subscribe via reqMktDepth (not L2). Keep stubbed but harmless.
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
            
            // Fix 3: Only update metrics if book is valid
            if (book.IsBookValid(out var validityReason, tsMs))
            {
                _metrics.UpdateMetrics(book, tsMs);
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
        => _logger.LogError("[IBKR Error {ErrorCode}] ID={Id}: {Message}", errorCode, id, errorMsg);

    public void error(string str) => _logger.LogError("[IBKR Error] {Message}", str);

    public void error(Exception e) => _logger.LogError(e, "[IBKR Exception]");

    public void currentTime(long time) { }

    // === Remaining EWrapper members (stubs) ===
    public void tickPrice(int tickerId, int field, double price, TickAttrib attribs) { }
    public void tickSize(int tickerId, int field, int size) { }
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


}
