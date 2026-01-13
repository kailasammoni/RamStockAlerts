using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using System.Xml;
using IBApi;

namespace ScannerDiagnostics;

internal sealed class ScannerClient : EWrapper, IDisposable
{
    private readonly IbkrConfig _config;
    private readonly object _sync = new();
    private EClientSocket? _socket;
    private EReaderSignal? _signal;
    private Task? _readerTask;
    private int _nextRequestId = 10_000;
    private TaskCompletionSource<string>? _paramsTcs;
    private TaskCompletionSource<IReadOnlyList<ScannerRow>>? _scanTcs;
    private TaskCompletionSource<IReadOnlyList<ContractDetailsInfo>>? _contractTcs;
    private readonly List<ScannerRow> _scanRows = new();
    private readonly List<ContractDetailsInfo> _contractRows = new();

    public ScannerClient(IbkrConfig config)
    {
        _config = config;
    }

    public async Task ConnectAsync(TimeSpan timeout)
    {
        _signal = new EReaderMonitorSignal();
        _socket = new EClientSocket(this, _signal);
        _socket.eConnect(_config.Host, _config.Port, _config.ClientId);

        if (!_socket.IsConnected())
        {
            throw new InvalidOperationException($"Failed to connect to IBKR at {_config.Host}:{_config.Port}");
        }

        _socket.startApi();
        var reader = new EReader(_socket, _signal);
        reader.Start();

        _readerTask = Task.Run(() =>
        {
            while (_socket.IsConnected())
            {
                _signal.waitForSignal();
                reader.processMsgs();
            }
        });

        using var cts = new CancellationTokenSource(timeout);
        await Task.Delay(10, cts.Token); // small yield to allow connection
    }

    public Task DisconnectAsync()
    {
        try
        {
            _socket?.eDisconnect();
        }
        catch
        {
        }

        return _readerTask ?? Task.CompletedTask;
    }

    public void Dispose()
    {
        _socket?.eDisconnect();
        _readerTask?.Wait(TimeSpan.FromSeconds(1));
    }

    public async Task<string> GetScannerParametersAsync(TimeSpan timeout)
    {
        EnsureConnected();

        lock (_sync)
        {
            _paramsTcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        _socket!.reqScannerParameters();
        return await AwaitWithTimeout(_paramsTcs, timeout, "scanner parameters");
    }

    public async Task<ScannerRunResult> RunScanAsync(ScanConfig config, DiagnosticsConfig diag, TimeSpan timeout)
    {
        EnsureConnected();

        var reqId = Interlocked.Increment(ref _nextRequestId);
        lock (_sync)
        {
            _scanRows.Clear();
            _scanTcs = new TaskCompletionSource<IReadOnlyList<ScannerRow>>(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        var subscription = new ScannerSubscription
        {
            Instrument = config.Instrument,
            LocationCode = config.LocationCode,
            ScanCode = config.ScanCode,
            NumberOfRows = diag.MaxRows
        };

        if (config.PriceAbove.HasValue)
        {
            subscription.AbovePrice = (double)config.PriceAbove.Value;
        }

        if (config.PriceBelow.HasValue)
        {
            subscription.BelowPrice = (double)config.PriceBelow.Value;
        }

        if (config.VolumeAbove.HasValue)
        {
            subscription.AboveVolume = (int)Math.Round(config.VolumeAbove.Value);
        }

        if (config.MarketCapAbove.HasValue)
        {
            subscription.MarketCapAbove = (double)config.MarketCapAbove.Value;
        }

        if (config.MarketCapBelow.HasValue)
        {
            subscription.MarketCapBelow = (double)config.MarketCapBelow.Value;
        }

        var filterOptions = BuildFilterOptions(config);
        _socket!.reqScannerSubscription(reqId, subscription, new List<TagValue>(), filterOptions);

        IReadOnlyList<ScannerRow> rows;
        try
        {
            rows = await AwaitWithTimeout(_scanTcs!, timeout, $"scan {config.Name}");
        }
        finally
        {
            try { _socket.cancelScannerSubscription(reqId); } catch { }
        }

        return new ScannerRunResult(config, rows.Take(diag.MaxRows).ToList());
    }

    public async Task<ContractDetailsInfo?> GetContractDetailsAsync(string symbol, string secType, DiagnosticsConfig diag)
    {
        EnsureConnected();

        var reqId = Interlocked.Increment(ref _nextRequestId);
        lock (_sync)
        {
            _contractRows.Clear();
            _contractTcs = new TaskCompletionSource<IReadOnlyList<ContractDetailsInfo>>(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        var contract = new Contract
        {
            Symbol = symbol,
            SecType = secType,
            Exchange = "SMART",
            Currency = "USD"
        };

        _socket!.reqContractDetails(reqId, contract);
        IReadOnlyList<ContractDetailsInfo> details;
        try
        {
            details = await AwaitWithTimeout(_contractTcs!, TimeSpan.FromMilliseconds(diag.RequestTimeoutMs), $"contract details {symbol}");
        }
        finally
        {
            try { _socket.cancelScannerSubscription(reqId); } catch { }
        }

        return details.FirstOrDefault();
    }

    private static async Task<T> AwaitWithTimeout<T>(TaskCompletionSource<T> tcs, TimeSpan timeout, string label)
    {
        using var cts = new CancellationTokenSource(timeout);
        var delay = Task.Delay(timeout, cts.Token);
        var completed = await Task.WhenAny(tcs.Task, delay);
        if (completed == delay)
        {
            throw new TimeoutException($"{label} timed out after {timeout.TotalSeconds:F0}s");
        }

        cts.Cancel();
        return await tcs.Task;
    }

    private void EnsureConnected()
    {
        if (_socket == null || !_socket.IsConnected())
        {
            throw new InvalidOperationException("Not connected to IBKR");
        }
    }

    // EWrapper implementation (only relevant handlers do work; others are no-ops)
    public void scannerParameters(string xml)
    {
        lock (_sync)
        {
            _paramsTcs?.TrySetResult(xml);
        }
    }

    public void scannerData(int reqId, int rank, ContractDetails contractDetails, string distance, string benchmark, string projection, string legsStr)
    {
        lock (_sync)
        {
            if (_scanTcs == null || _scanTcs.Task.IsCompleted)
            {
                return;
            }

            if (_scanRows.Count >= 500)
            {
                return; // bound output
            }

            var symbol = contractDetails.Contract?.Symbol ?? string.Empty;
            var secType = contractDetails.Contract?.SecType ?? string.Empty;
            _scanRows.Add(new ScannerRow(symbol, secType, rank));
        }
    }

    public void scannerDataEnd(int reqId)
    {
        lock (_sync)
        {
            _scanTcs?.TrySetResult(_scanRows.ToList());
        }
    }

    private List<TagValue> BuildFilterOptions(ScanConfig config)
    {
        var options = new List<TagValue>();

        if (config.FloatSharesBelow.HasValue)
        {
            options.Add(new TagValue("floatSharesBelow", config.FloatSharesBelow.Value.ToString(CultureInfo.InvariantCulture)));
        }

        return options;
    }

    public void contractDetails(int reqId, ContractDetails contractDetails)
    {
        lock (_sync)
        {
            if (_contractTcs == null || _contractTcs.Task.IsCompleted)
            {
                return;
            }

            var c = contractDetails.Contract;
            var info = new ContractDetailsInfo(
                c?.Symbol ?? string.Empty,
                c?.SecType ?? string.Empty,
                c?.ConId ?? 0,
                c?.Exchange,
                c?.PrimaryExch,
                c?.Currency,
                contractDetails.LongName,
                contractDetails.StockType,
                contractDetails.Category,
                contractDetails.Subcategory,
                contractDetails.DescAppend);

            _contractRows.Add(info);
        }
    }

    public void contractDetailsEnd(int reqId)
    {
        lock (_sync)
        {
            _contractTcs?.TrySetResult(_contractRows.ToList());
        }
    }

    public void error(int id, int errorCode, string errorMsg)
    {
        if (errorCode is 2104 or 2106 or 2158)
        {
            // Informational market data farm connection messages; ignore.
            return;
        }

        if (errorCode == 165)
        {
            Console.WriteLine($"[IBKR ScannerDiagnostics] Warning code={errorCode} msg={errorMsg} (ignoring)");
            return;
        }

        lock (_sync)
        {
            var ex = new InvalidOperationException($"IB error {errorCode}: {errorMsg}");
            _paramsTcs?.TrySetException(ex);
            _scanTcs?.TrySetException(ex);
            _contractTcs?.TrySetException(ex);
        }
    }

    public void error(Exception e) => error(-1, -1, e.Message);
    public void error(string str) => error(-1, -1, str);
    public void connectionClosed()
    {
        lock (_sync)
        {
            var ex = new InvalidOperationException("Connection closed");
            _paramsTcs?.TrySetException(ex);
            _scanTcs?.TrySetException(ex);
            _contractTcs?.TrySetException(ex);
        }
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
    public void execDetails(int reqId, Contract contract, Execution execution) { }
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
    public void marketDataType(int reqId, int marketDataType, string marketData) { }
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
}

internal sealed record ScannerRunResult(ScanConfig Config, IReadOnlyList<ScannerRow> Rows);

internal static class ScannerParamsParser
{
    internal sealed record Summary(
        IReadOnlyList<string> Instruments,
        IReadOnlyList<string> Locations,
        bool StockTypeFilterSupported,
        IReadOnlyList<string> StockTypeValues);

    public static Summary Parse(string xml)
    {
        var doc = new XmlDocument();
        doc.LoadXml(xml);

        var instruments = new List<string>();
        var locations = new List<string>();
        var stockTypes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var stockFilterSupported = false;

        foreach (XmlNode node in doc.GetElementsByTagName("instrument"))
        {
            if (node?.InnerText is { Length: > 0 } text && text.Contains("STK", StringComparison.OrdinalIgnoreCase))
            {
                instruments.Add(text.Trim());
            }
        }

        foreach (XmlNode node in doc.GetElementsByTagName("locationCode"))
        {
            if (node?.InnerText is { Length: > 0 } text &&
                (text.Contains("STK.US", StringComparison.OrdinalIgnoreCase) ||
                 text.Contains("STK.US.MAJOR", StringComparison.OrdinalIgnoreCase) ||
                 text.Contains("STK.NASDAQ", StringComparison.OrdinalIgnoreCase)))
            {
                locations.Add(text.Trim());
            }
        }

        foreach (XmlNode node in doc.GetElementsByTagName("scanParam"))
        {
            var tag = node.Attributes?["tag"]?.Value;
            if (tag != null && tag.Equals("stockTypeFilter", StringComparison.OrdinalIgnoreCase))
            {
                stockFilterSupported = true;
                foreach (XmlNode child in node.ChildNodes)
                {
                    if (child.Name.Equals("String", StringComparison.OrdinalIgnoreCase) &&
                        !string.IsNullOrWhiteSpace(child.InnerText))
                    {
                        stockTypes.Add(child.InnerText.Trim());
                    }
                }
            }
        }

        return new Summary(instruments, locations, stockFilterSupported, stockTypes.ToList());
    }
}
