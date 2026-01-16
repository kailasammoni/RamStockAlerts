using System.Diagnostics;
using IBApi;

namespace ScannerDiagnostics;

/// <summary>
/// Tests IBKR subscription limits by attempting depth, tick-by-tick, and market data subscriptions
/// on a hardcoded list of symbols. Provides hard evidence of which data tiers work.
/// </summary>
internal sealed class SubscriptionDiagnosticsTool : EWrapper, IDisposable
{
    private readonly IbkrConfig _config;
    private readonly object _sync = new();
    private EClientSocket? _socket;
    private EReaderSignal? _signal;
    private Task? _readerTask;
    private int _nextRequestId = 5000;
    
    private readonly Dictionary<int, (string Symbol, SubscriptionType Type)> _requestMap = new();
    private readonly Dictionary<string, SymbolSubscriptionResult> _results = new();
    private readonly List<IbkrError> _errors = new();
    
    public SubscriptionDiagnosticsTool(IbkrConfig config)
    {
        _config = config;
    }

    public async Task<SubscriptionTestResults> RunTestAsync(
        IReadOnlyList<string> symbols,
        TimeSpan timeout)
    {
        await ConnectAsync(timeout);
        
        try
        {
            // Initialize results for all symbols
            foreach (var symbol in symbols)
            {
                _results[symbol] = new SymbolSubscriptionResult { Symbol = symbol };
            }

            // Attempt depth subscriptions for all symbols
            Console.WriteLine($"\n[DEPTH TEST] Attempting depth subscriptions for {symbols.Count} symbols...");
            var depthStartTime = Stopwatch.StartNew();
            foreach (var symbol in symbols)
            {
                var mktDataId = _nextRequestId++;
                var depthId = _nextRequestId++;
                
                var contract = new Contract { Symbol = symbol, SecType = "STK", Exchange = "NASDAQ", Currency = "USD" };
                _socket?.reqMktData(mktDataId, contract, "", false, false, null);
                _socket?.reqMarketDepth(depthId, contract, 5, false, null);
                
                _requestMap[mktDataId] = (symbol, SubscriptionType.MktData);
                _requestMap[depthId] = (symbol, SubscriptionType.Depth);
                _results[symbol].MktDataRequestId = mktDataId;
                _results[symbol].DepthRequestId = depthId;
                
                await Task.Delay(50); // Small delay between requests to see errors sequentially
            }
            depthStartTime.Stop();
            await Task.Delay((int)timeout.TotalMilliseconds / 2); // Wait for responses
            Console.WriteLine($"  ✓ Depth subscription attempts completed in {depthStartTime.ElapsedMilliseconds}ms");

            // Attempt tick-by-tick subscriptions for all symbols
            Console.WriteLine($"\n[TICK-BY-TICK TEST] Attempting tick-by-tick subscriptions for {symbols.Count} symbols...");
            var tickByTickStartTime = Stopwatch.StartNew();
            foreach (var symbol in symbols)
            {
                var tickByTickId = _nextRequestId++;
                
                var contract = new Contract { Symbol = symbol, SecType = "STK", Exchange = "NASDAQ", Currency = "USD" };
                _socket?.reqTickByTickData(tickByTickId, contract, "Last", 0, false);
                
                _requestMap[tickByTickId] = (symbol, SubscriptionType.TickByTick);
                _results[symbol].TickByTickRequestId = tickByTickId;
                
                await Task.Delay(50); // Small delay between requests to see errors sequentially
            }
            tickByTickStartTime.Stop();
            await Task.Delay((int)timeout.TotalMilliseconds / 2); // Wait for responses
            Console.WriteLine($"  ✓ Tick-by-tick subscription attempts completed in {tickByTickStartTime.ElapsedMilliseconds}ms");

            await Task.Delay(2000); // Final wait to catch any lingering responses

            return BuildResults();
        }
        finally
        {
            await DisconnectAsync();
        }
    }

    private async Task ConnectAsync(TimeSpan timeout)
    {
        _signal = new EReaderMonitorSignal();
        _socket = new EClientSocket(this, _signal);
        _socket.eConnect(_config.Host, _config.Port, _config.ClientId + 100);

        if (!_socket.IsConnected())
        {
            throw new InvalidOperationException(
                $"Failed to connect to IBKR at {_config.Host}:{_config.Port}");
        }

        _socket.startApi();
        var reader = new EReader(_socket, _signal);
        reader.Start();

        _readerTask = Task.Run(() =>
        {
            while (_socket?.IsConnected() ?? false)
            {
                _signal?.waitForSignal();
                reader.processMsgs();
            }
        });

        await Task.Delay(500); // Give connection time to establish
    }

    private async Task DisconnectAsync()
    {
        if (_socket?.IsConnected() ?? false)
        {
            _socket.eDisconnect();
        }

        if (_readerTask != null)
        {
            await _readerTask;
        }
    }

    public void error(int id, int errorCode, string errorMsg)
    {
        lock (_sync)
        {
            if (_requestMap.TryGetValue(id, out var request))
            {
                var symbol = request.Symbol;
                var type = request.Type;
                
                _errors.Add(new IbkrError 
                { 
                    RequestId = id, 
                    ErrorCode = errorCode, 
                    ErrorMessage = errorMsg,
                    Symbol = symbol,
                    SubscriptionType = type
                });

                if (_results.TryGetValue(symbol, out var result))
                {
                    switch (type)
                    {
                        case SubscriptionType.Depth:
                            result.DepthSuccess = false;
                            result.DepthErrorCode = errorCode;
                            result.DepthErrorMessage = errorMsg;
                            break;
                        case SubscriptionType.TickByTick:
                            result.TickByTickSuccess = false;
                            result.TickByTickErrorCode = errorCode;
                            result.TickByTickErrorMessage = errorMsg;
                            break;
                    }
                }

                Console.WriteLine($"  [ERROR {errorCode}] {type} for {symbol} (ID={id}): {errorMsg}");
            }
            else
            {
                Console.WriteLine($"  [ERROR {errorCode}] ID={id}: {errorMsg}");
            }
        }
    }

    public void mktDepth(int tickerId, int position, int operation, int side, double price, Decimal size)
    {
        lock (_sync)
        {
            if (_requestMap.TryGetValue(tickerId, out var request) && request.Type == SubscriptionType.Depth)
            {
                var symbol = request.Symbol;
                if (_results.TryGetValue(symbol, out var result))
                {
                    result.DepthSuccess = true;
                    result.DepthDataReceived = true;
                }
            }
        }
    }

    public void tickByTickAll(int reqId, int tickType, long time, double price, Decimal size, TickAttrib attribs, string exchange, string specialConditions)
    {
        lock (_sync)
        {
            if (_requestMap.TryGetValue(reqId, out var request) && request.Type == SubscriptionType.TickByTick)
            {
                var symbol = request.Symbol;
                if (_results.TryGetValue(symbol, out var result))
                {
                    result.TickByTickSuccess = true;
                    result.TickByTickDataReceived = true;
                }
            }
        }
    }

    private SubscriptionTestResults BuildResults()
    {
        var results = _results.Values.ToList();
        
        var depthSuccessCount = results.Count(r => r.DepthSuccess ?? false);
        var tickByTickSuccessCount = results.Count(r => r.TickByTickSuccess ?? false);
        var tapeOnlyCount = results.Count(r => (r.DepthSuccess != true && r.TickByTickSuccess != true));
        var depthPlusTick = results.Count(r => (r.DepthSuccess == true && r.TickByTickSuccess == true));

        return new SubscriptionTestResults
        {
            TestTimestamp = DateTimeOffset.UtcNow,
            TotalSymbols = results.Count,
            Results = results.OrderBy(r => r.Symbol).ToList(),
            Errors = _errors,
            Summary = new TestSummary
            {
                DepthOnlySuccess = depthSuccessCount - depthPlusTick,
                DepthPlusTickByTickSuccess = depthPlusTick,
                TickByTickOnlySuccess = tickByTickSuccessCount - depthPlusTick,
                TapeOnlyFallback = tapeOnlyCount,
                IbkrHardLimits = new()
                {
                    MaxConcurrentDepth = 3,
                    MaxConcurrentTickByTick = 6,
                    UnlimitedMarketDataTape = true
                }
            }
        };
    }

    public void Dispose()
    {
        // EClientSocket and EReaderMonitorSignal don't support Dispose
    }

    // Unused callbacks (required by interface)
    public void currentTime(long time) { }
    public void nextValidId(int orderId) { }
    public void tickPrice(int tickerId, int field, double price, TickAttrib attribs) { }
    public void tickSize(int tickerId, int field, int size) { }
    public void tickOptionComputation(int tickerId, int field, double impliedVol, double delta, double optPrice, double pvDividend, double gamma, double vega, double theta, double undPrice) { }
    public void tickGeneric(int tickerId, int field, double value) { }
    public void tickString(int tickerId, int field, string value) { }
    public void tickEFP(int tickerId, int tickType, double basisPoints, string formattedBasisPoints, double impliedFuture, int holdDays, string futureExpiry, double dividendImpact, double dividendsToExpiry) { }
    public void orderStatus(int orderId, string status, double filled, double remaining, double avgFillPrice, int permId, int parentId, double lastFillPrice, int clientId, string whyHeld, double mktCapPrice) { }
    public void openOrder(int orderId, Contract contract, Order order, OrderState orderState) { }
    public void openOrderEnd() { }
    public void winError(string str, int lastError) { }
    public void connectionStatus(bool isConnected) { }
    public void accountSummary(int reqId, string account, string tag, string value, string currency) { }
    public void accountSummaryEnd(int reqId) { }
    public void updateAccountValue(string key, string value, string currency, string accountName) { }
    public void updatePortfolio(Contract contract, double position, double marketPrice, double marketValue, double averageCost, double unrealizedPNL, double realizedPNL, string accountName) { }
    public void updateAccountTime(string timestamp) { }
    public void accountDownloadEnd(string accountName) { }
    public void bondContractDetails(int reqId, ContractDetails contractDetails) { }
    public void execDetails(int reqId, Contract contract, IBApi.Execution execution) { }
    public void execDetailsEnd(int reqId) { }
    public void commissionReport(CommissionReport commissionReport) { }
    public void fundamentalData(int reqId, string data) { }
    public void historicalData(int reqId, Bar bar) { }
    public void historicalDataEnd(int reqId, string start, string end) { }
    public void historicalDataUpdate(int reqId, Bar bar) { }
    public void marketDataType(int reqId, int marketDataType) { }
    public void realtimeBar(int reqId, long time, double open, double high, double low, double close, long volume, double wap, int count) { }
    public void updateMktDepth(int tickerId, int position, int operation, int side, double price, int size) { }
    public void updateMktDepthL2(int tickerId, int position, string marketMaker, int operation, int side, double price, int size, bool isSmartDepth) { }
    public void updateNewsBulletin(int newsMsgId, int newsMsgType, string newsMessage, string originExch) { }
    public void managedAccounts(string accountsList) { }
    public void receiveFA(int faDataType, string faXmlData) { }
    public void tickReqParams(int tickerId, double minTick, string bboExchange, int snapshotPermissions) { }
    public void mktDepthExchanges(DepthMktDataDescription[] depthMktDataDescriptions) { }
    public void tickSnapshotEnd(int tickerId) { }
    public void marketRule(int marketRuleId, PriceIncrement[] priceIncrements) { }
    public void pnl(int reqId, double dailyPnL, double unrealizedPnL, double realizedPnL) { }
    public void pnlSingle(int reqId, int pos, double dailyPnL, double unrealizedPnL, double realizedPnL, double value) { }
    public void verifyMessageAPI(string apiData) { }
    public void verifyCompleted(bool isSuccessful, string errorText) { }
    public void verifyAndAuthMessageAPI(string apiData, string xyzChallange) { }
    public void verifyAndAuthCompleted(bool isSuccessful, string errorText) { }
    public void displayGroupList(int reqId, string groups) { }
    public void displayGroupUpdated(int reqId, string contractInfo) { }
    public void connectAck() { }
    public void connectionClosed() { }
    public void historicalTicks(int reqId, HistoricalTick[] ticks, bool done) { }
    public void historicalTicksBidAsk(int reqId, HistoricalTickBidAsk[] ticks, bool done) { }
    public void historicalTicksLast(int reqId, HistoricalTickLast[] ticks, bool done) { }
    public void tickByTickAllLast(int reqId, int tickType, long time, double price, int size, TickAttribLast tickAttribLast, string exchange, string specialConditions) { }
    public void tickByTickBidAsk(int reqId, long time, double bidPrice, double askPrice, int bidSize, int askSize, TickAttribBidAsk tickAttribBidAsk) { }
    public void tickByTickMidPoint(int reqId, long time, double midPoint) { }
    public void headTimestamp(int reqId, string headTimestamp) { }
    public void histogramData(int reqId, HistogramEntry[] data) { }
    public void rerouteMktDataReq(int reqId, int conId, string exchange) { }
    public void rerouteMktDepthReq(int reqId, int conId, string exchange) { }
    public void tickNews(int tickerId, long timeStamp, string providerCode, string articleId, string headline, string extraData) { }
    public void smartComponents(int reqId, Dictionary<int, KeyValuePair<string, char>> theMap) { }
    public void newsProviders(NewsProvider[] newsProviders) { }
    public void newsArticle(int requestId, int articleType, string articleText) { }
    public void historicalNews(int requestId, string time, string providerCode, string articleId, string headline) { }
    public void historicalNewsEnd(int requestId, bool hasMore) { }
    public void headTimestamp(int reqId, string headTimestamp, string headTimestampType) { }
    public void histogramData(int reqId, HistogramEntry[] data, string histogramDataType) { }
    public void historicalTicks(int reqId, HistoricalTick[] ticks, bool done, string historicalTickType) { }
    public void historicalTicksBidAsk(int reqId, HistoricalTickBidAsk[] ticks, bool done, string historicalTickType) { }
    public void historicalTicksLast(int reqId, HistoricalTickLast[] ticks, bool done, string historicalTickType) { }
    public void tickOptionComputation(int tickerId, int field, double impliedVol, double delta, double optPrice, double pvDividend, double gamma, double vega, double theta, double undPrice, double futurePrice) { }
    public void deltaNeutralValidation(int reqId, DeltaNeutralContract deltaNeutralContract) { }
    public void tickByTickAllLast(int reqId, int tickType, long time, double price, int size, TickAttribLast tickAttribLast, string exchange, string specialConditions, bool delayed) { }
    public void tickByTickBidAsk(int reqId, long time, double bidPrice, double askPrice, int bidSize, int askSize, TickAttribBidAsk tickAttribBidAsk, bool delayed) { }
    public void tickByTickMidPoint(int reqId, long time, double midPoint, bool delayed) { }
    public void orderBound(long orderId, int apiClientId, int apiOrderId, string status) { }
    public void orderBound(long orderId, int apiClientId, int apiOrderId) { }
    public void completedOrder(Contract contract, Order order, OrderState orderState, string orderProcessorStatus) { }
    public void completedOrder(Contract contract, Order order, OrderState orderState) { }
    public void completedOrdersEnd(string orderProcessorStatus) { }
    public void completedOrdersEnd() { }
    public void position(string account, Contract contract, double pos, double avgCost) { }
    public void positionEnd() { }
    public void securityDefinitionOptionParameter(int reqId, string exchange, int underlyingConId, string tradingClass, string multiplier, HashSet<string> expirations, HashSet<double> strikes) { }
    public void securityDefinitionOptionParameterEnd(int reqId) { }
    public void softDollarTiers(int reqId, SoftDollarTier[] tiers) { }
    public void familyCodes(FamilyCode[] familyCodes) { }
    public void symbolSamples(int reqId, ContractDescription[] contractDescriptions) { }
    public void positionMulti(int reqId, string account, string modelCode, Contract contract, double pos, double avgCost) { }
    public void positionMultiEnd(int reqId) { }
    public void accountUpdateMulti(int reqId, string account, string modelCode, string key, string value, string currency) { }
    public void accountUpdateMultiEnd(int reqId) { }
    public void realtimeBar(int reqId, long time, double open, double high, double low, double close, long volume, double wap, int count, string realTimeBarType) { }
    public void historicalDataUpdate(int reqId, HistoricalTick[] ticks) { }
    public void error(Exception e) => error(-1, -1, e.Message);
    public void error(string str) => error(-1, -1, str);
    public void contractDetails(int reqId, ContractDetails contractDetails) { }
    public void contractDetailsEnd(int reqId) { }
    public void scannerParameters(string xml) { }
    public void scannerData(int reqId, int rank, ContractDetails contractDetails, string leaderboardName, string leaderboardValue, string marketName, string marketValue) { }
    public void scannerDataEnd(int reqId) { }
    public void receiptOfFunds(string fundsList) { }
}

public enum SubscriptionType
{
    MktData,
    Depth,
    TickByTick
}

public sealed record SymbolSubscriptionResult
{
    public string Symbol { get; set; } = string.Empty;
    public int? MktDataRequestId { get; set; }
    public int? DepthRequestId { get; set; }
    public int? TickByTickRequestId { get; set; }
    
    public bool? DepthSuccess { get; set; }
    public bool DepthDataReceived { get; set; }
    public int? DepthErrorCode { get; set; }
    public string? DepthErrorMessage { get; set; }
    
    public bool? TickByTickSuccess { get; set; }
    public bool TickByTickDataReceived { get; set; }
    public int? TickByTickErrorCode { get; set; }
    public string? TickByTickErrorMessage { get; set; }
}

public sealed record IbkrError
{
    public int RequestId { get; set; }
    public int ErrorCode { get; set; }
    public string ErrorMessage { get; set; } = string.Empty;
    public string Symbol { get; set; } = string.Empty;
    public SubscriptionType SubscriptionType { get; set; }
}

public sealed record SubscriptionTestResults
{
    public DateTimeOffset TestTimestamp { get; set; }
    public int TotalSymbols { get; set; }
    public List<SymbolSubscriptionResult> Results { get; set; } = new();
    public List<IbkrError> Errors { get; set; } = new();
    public TestSummary Summary { get; set; } = new();
}

public sealed record TestSummary
{
    public int DepthOnlySuccess { get; set; }
    public int DepthPlusTickByTickSuccess { get; set; }
    public int TickByTickOnlySuccess { get; set; }
    public int TapeOnlyFallback { get; set; }
    public IbkrHardLimits IbkrHardLimits { get; set; } = new();
}

public sealed record IbkrHardLimits
{
    public int MaxConcurrentDepth { get; set; }
    public int MaxConcurrentTickByTick { get; set; }
    public bool UnlimitedMarketDataTape { get; set; }
}
