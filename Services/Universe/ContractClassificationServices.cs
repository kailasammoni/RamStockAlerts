using System.Collections.Concurrent;
using System.Text.Json;
using System.Linq;
using IBApi;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace RamStockAlerts.Services.Universe;

public sealed record ContractClassification(
    string Symbol,
    int ConId,
    string? SecType,
    string? PrimaryExchange,
    string? Currency,
    string StockType,
    DateTimeOffset UpdatedAt);

public enum ContractSecurityClassification
{
    Unknown = 0,
    CommonStock = 1,
    Etf = 2,
    Etn = 3,
    Other = 4
}

public sealed class ContractClassificationCache
{
    private readonly ILogger<ContractClassificationCache> _logger;
    private readonly ConcurrentDictionary<string, ContractClassification> _cache = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _sync = new(1, 1);
    private readonly TimeSpan _ttl;
    private readonly string? _persistencePath;

    public ContractClassificationCache(IConfiguration configuration, ILogger<ContractClassificationCache> logger)
    {
        _logger = logger;
        _ttl = TimeSpan.FromHours(Math.Max(1, configuration.GetValue("Universe:ClassificationTtlHours", 24)));
        _persistencePath = configuration["Universe:ClassificationCacheFile"];
        LoadFromDisk();
    }

    public bool TryGet(string symbol, DateTimeOffset asOf, out ContractClassification classification)
    {
        classification = default!;
        if (string.IsNullOrWhiteSpace(symbol))
        {
            return false;
        }

        var key = NormalizeSymbol(symbol);
        if (_cache.TryGetValue(key, out var entry))
        {
            if (!IsExpired(entry, asOf))
            {
                classification = entry;
                return true;
            }

            _cache.TryRemove(key, out _);
        }

        return false;
    }

    public ContractClassification? TryGetCached(string symbol)
    {
        return TryGet(symbol, DateTimeOffset.UtcNow, out var entry) ? entry : null;
    }

    public async Task PutAsync(ContractClassification classification, CancellationToken cancellationToken)
    {
        var normalizedSymbol = NormalizeSymbol(classification.Symbol);
        var normalized = classification with
        {
            Symbol = normalizedSymbol,
            UpdatedAt = classification.UpdatedAt == default ? DateTimeOffset.UtcNow : classification.UpdatedAt
        };

        _cache[normalizedSymbol] = normalized;

        if (string.IsNullOrWhiteSpace(_persistencePath))
        {
            return;
        }

        await _sync.WaitAsync(cancellationToken);
        try
        {
            var directory = Path.GetDirectoryName(_persistencePath);
            if (!string.IsNullOrWhiteSpace(directory))
            {
                Directory.CreateDirectory(directory);
            }
            var payload = JsonSerializer.Serialize(_cache.Values.ToArray());
            await File.WriteAllTextAsync(_persistencePath, payload, cancellationToken);
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning(ex, "Failed to persist contract classifications to {Path}", _persistencePath);
        }
        finally
        {
            _sync.Release();
        }
    }

    public ContractSecurityClassification Classify(ContractClassification? classification)
    {
        if (classification is null || string.IsNullOrWhiteSpace(classification.StockType))
        {
            return ContractSecurityClassification.Unknown;
        }

        var stockType = classification.StockType.Trim().ToUpperInvariant();
        if (stockType == "COMMON" || stockType == "CS" || stockType == "STK")
        {
            return ContractSecurityClassification.CommonStock;
        }

        if (stockType == "UNKNOWN")
        {
            return ContractSecurityClassification.Unknown;
        }

        if (stockType == "ETF")
        {
            return ContractSecurityClassification.Etf;
        }

        if (stockType == "ETN" || stockType == "ETP")
        {
            return ContractSecurityClassification.Etn;
        }

        return ContractSecurityClassification.Other;
    }

    private void LoadFromDisk()
    {
        if (string.IsNullOrWhiteSpace(_persistencePath) || !File.Exists(_persistencePath))
        {
            return;
        }

        try
        {
            var json = File.ReadAllText(_persistencePath);
            var entries = JsonSerializer.Deserialize<List<ContractClassification>>(json);
            if (entries is null)
            {
                return;
            }

            var now = DateTimeOffset.UtcNow;
            foreach (var entry in entries)
            {
                if (entry is null || IsExpired(entry, now))
                {
                    continue;
                }

                _cache[NormalizeSymbol(entry.Symbol)] = entry;
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to load persisted contract classifications from {Path}", _persistencePath);
        }
    }

    private bool IsExpired(ContractClassification entry, DateTimeOffset asOf)
    {
        return asOf - entry.UpdatedAt > _ttl;
    }

    private static string NormalizeSymbol(string symbol)
    {
        return symbol.Trim().ToUpperInvariant();
    }
}

public sealed class ContractClassificationService
{
    private static int _nextRequestId = 60_000;

    private readonly IConfiguration _configuration;
    private readonly ILogger<ContractClassificationService> _logger;
    private readonly ContractClassificationCache _cache;
    private readonly TimeSpan _minInterval;
    private readonly SemaphoreSlim _throttle = new(1, 1);
    private readonly SemaphoreSlim _connectionLock = new(1, 1);
    private DateTimeOffset _lastRequestAtUtc = DateTimeOffset.MinValue;

    public ContractClassificationService(
        IConfiguration configuration,
        ILogger<ContractClassificationService> logger,
        ContractClassificationCache cache)
    {
        _configuration = configuration;
        _logger = logger;
        _cache = cache;
        var minSeconds = Math.Max(1, configuration.GetValue("Universe:ContractDetails:MinIntervalSeconds", 1));
        _minInterval = TimeSpan.FromSeconds(minSeconds);
    }

    public async Task<IReadOnlyDictionary<string, ContractClassification>> GetClassificationsAsync(
        IEnumerable<string> symbols,
        CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        var result = new Dictionary<string, ContractClassification>(StringComparer.OrdinalIgnoreCase);
        var missing = new List<string>();

        foreach (var symbol in symbols)
        {
            if (string.IsNullOrWhiteSpace(symbol))
            {
                continue;
            }

            var normalized = symbol.Trim().ToUpperInvariant();
            if (_cache.TryGet(normalized, now, out var cached))
            {
                result[normalized] = cached;
                continue;
            }

            if (!missing.Contains(normalized, StringComparer.OrdinalIgnoreCase))
            {
                missing.Add(normalized);
            }
        }

        if (missing.Count == 0)
        {
            return result;
        }

        var fetched = await FetchFromIbkrAsync(missing, cancellationToken);
        foreach (var classification in fetched)
        {
            result[classification.Symbol] = classification;
        }

        return result;
    }

    public ContractSecurityClassification Classify(ContractClassification? classification)
    {
        return _cache.Classify(classification);
    }

    public ContractClassification? TryGetCached(string symbol)
    {
        return _cache.TryGetCached(symbol);
    }

    public async Task CacheAsync(ContractClassification classification, CancellationToken cancellationToken)
    {
        await _cache.PutAsync(classification, cancellationToken);
    }

    private async Task<List<ContractClassification>> FetchFromIbkrAsync(
        List<string> symbols,
        CancellationToken cancellationToken)
    {
        _logger.LogInformation("[IBKR ContractDetails] FetchFromIbkrAsync called for {Count} symbols", symbols.Count);
        
        await _connectionLock.WaitAsync(cancellationToken);
        var classifications = new List<ContractClassification>();
        var host = _configuration["IBKR:Host"] ?? _configuration["Ibkr:Host"] ?? "127.0.0.1";
        var port = _configuration.GetValue<int?>("IBKR:Port")
                   ?? _configuration.GetValue<int?>("Ibkr:Port")
                   ?? 7496;
        var baseClientId = _configuration.GetValue<int?>("IBKR:ClientId")
                         ?? _configuration.GetValue<int?>("Ibkr:ClientId")
                         ?? 1;
        var clientId = _configuration.GetValue<int?>("Universe:IbkrContract:ClientId") ?? baseClientId + 2;

        var readerSignal = new EReaderMonitorSignal();
        var wrapper = new ContractDetailsWrapper(_logger);
        var client = new EClientSocket(wrapper, readerSignal);

        _logger.LogInformation(
            "[IBKR ContractDetails] Connecting to {Host}:{Port} clientId={ClientId}",
            host,
            port,
            clientId);

        client.eConnect(host, port, clientId);

        if (!client.IsConnected())
        {
            _logger.LogWarning("[IBKR ContractDetails] Failed to connect to {Host}:{Port}", host, port);
            return classifications;
        }

        client.startApi();

        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var pumpTask = Task.Run(() => ProcessMessages(client, readerSignal, wrapper, linkedCts.Token), linkedCts.Token);

        try
        {
            foreach (var symbol in symbols)
            {
                await EnforceThrottleAsync(cancellationToken);

                var requestId = Interlocked.Increment(ref _nextRequestId);
                var task = wrapper.Register(requestId, symbol);

                var contract = new Contract
                {
                    Symbol = symbol,
                    SecType = "STK",
                    Exchange = "SMART",
                    Currency = "USD"
                };

                client.reqContractDetails(requestId, contract);

                var completed = await Task.WhenAny(
                    task,
                    Task.Delay(TimeSpan.FromSeconds(5), cancellationToken));

                if (completed != task || !task.IsCompletedSuccessfully)
                {
                    _logger.LogInformation("[IBKR ContractDetails] Timeout for {Symbol}", symbol);
                    continue;
                }

                var details = task.Result;
                var now = DateTimeOffset.UtcNow;
                var classification = ToClassification(details, now);
                await _cache.PutAsync(classification, cancellationToken);
                classifications.Add(classification);
            }
        }
        finally
        {
            _connectionLock.Release();
            try
            {
                linkedCts.Cancel();
            }
            catch
            {
            }

            if (client.IsConnected())
            {
                try
                {
                    client.eDisconnect();
                }
                catch
                {
                }
            }

            try
            {
                readerSignal.issueSignal();
            }
            catch
            {
            }

            try
            {
                await pumpTask;
            }
            catch
            {
            }
        }

        return classifications;
    }

    private async Task EnforceThrottleAsync(CancellationToken cancellationToken)
    {
        await _throttle.WaitAsync(cancellationToken);
        try
        {
            var now = DateTimeOffset.UtcNow;
            var delay = _lastRequestAtUtc + _minInterval - now;
            if (delay > TimeSpan.Zero)
            {
                await Task.Delay(delay, cancellationToken);
            }

            _lastRequestAtUtc = DateTimeOffset.UtcNow;
        }
        finally
        {
            _throttle.Release();
        }
    }

    private static ContractClassification ToClassification(ContractDetails details, DateTimeOffset now)
    {
        var symbol = details.Contract?.Symbol ?? string.Empty;
        var stockType = ResolveStockType(details);
        return new ContractClassification(
            symbol.Trim().ToUpperInvariant(),
            details.Contract?.ConId ?? 0,
            details.Contract?.SecType,
            details.Contract?.PrimaryExch,
            details.Contract?.Currency,
            stockType,
            now);
    }

    private static string ResolveStockType(ContractDetails details)
    {
        if (!string.IsNullOrWhiteSpace(details.StockType))
        {
            return details.StockType.Trim().ToUpperInvariant();
        }

        var secType = details.Contract?.SecType?.Trim();
        if (!string.IsNullOrWhiteSpace(secType) && !secType.Equals("STK", StringComparison.OrdinalIgnoreCase))
        {
            return secType.ToUpperInvariant();
        }

        return "UNKNOWN";
    }

    private static void ProcessMessages(
        EClientSocket socket,
        EReaderSignal signal,
        ContractDetailsWrapper wrapper,
        CancellationToken stoppingToken)
    {
        var reader = new EReader(socket, signal);
        reader.Start();

        using var _ = stoppingToken.Register(() =>
        {
            try
            {
                signal.issueSignal();
            }
            catch
            {
            }
        });

        while (!stoppingToken.IsCancellationRequested && socket.IsConnected())
        {
            try
            {
                signal.waitForSignal();
                reader.processMsgs();
            }
            catch
            {
                break;
            }
        }
    }

    private sealed class ContractDetailsWrapper : EWrapper
    {
        private readonly ILogger _logger;
        private readonly ConcurrentDictionary<int, TaskCompletionSource<ContractDetails>> _pending = new();

        public ContractDetailsWrapper(ILogger logger)
        {
            _logger = logger;
        }

        public Task<ContractDetails> Register(int requestId, string symbol)
        {
            var tcs = new TaskCompletionSource<ContractDetails>(TaskCreationOptions.RunContinuationsAsynchronously);
            _pending[requestId] = tcs;
            return tcs.Task;
        }

        public void contractDetails(int reqId, ContractDetails contractDetails)
        {
            if (_pending.TryRemove(reqId, out var tcs) && contractDetails is not null)
            {
                tcs.TrySetResult(contractDetails);
            }
        }

        public void contractDetailsEnd(int reqId)
        {
            if (_pending.TryRemove(reqId, out var tcs))
            {
                tcs.TrySetCanceled();
            }
        }

        public void error(int id, int errorCode, string errorMsg)
        {
            if (errorCode == 2104 || errorCode == 2106 || errorCode == 2158)
            {
                _logger.LogDebug("[IBKR ContractDetails] Info: id={Id} code={Code} msg={Msg}", id, errorCode, errorMsg);
                return;
            }

            if (id <= 0)
            {
                return;
            }

            if (_pending.TryRemove(id, out var tcs))
            {
                tcs.TrySetException(new InvalidOperationException($"IBKR error {errorCode}: {errorMsg}"));
            }
        }

        public void error(string str) => _logger.LogDebug("[IBKR ContractDetails] {Message}", str);
        public void error(Exception e) => _logger.LogDebug(e, "[IBKR ContractDetails] Exception");

        public void currentTime(long time) { }
        public void tickPrice(int tickerId, int field, double price, TickAttrib attribs) { }
        public void tickSize(int tickerId, int field, int size) { }
        public void tickOptionComputation(int tickerId, int field, double impliedVol, double delta, double optPrice, double pvDividend, double gamma, double vega, double theta, double undPrice) { }
        public void tickGeneric(int tickerId, int tickType, double value) { }
        public void tickString(int tickerId, int tickType, string value) { }
        public void tickEFP(int tickerId, int tickType, double basisPoints, string formattedBasisPoints, double impliedFuture, int holdDays, string futureExpiry, double dividendImpact, double dividendsToExpiry) { }
        public void orderStatus(int orderId, string status, double filled, double remaining, double avgFillPrice, int permId, int parentId, double lastFillPrice, int clientId, string whyHeld, double mktCapPrice) { }
        public void openOrder(int orderId, Contract contract, Order order, OrderState orderState) { }
        public void openOrderEnd() { }
        public void updateAccountValue(string key, string value, string currency, string accountName) { }
        public void updatePortfolio(Contract contract, double position, double marketPrice, double marketValue, double averageCost, double unrealizedPNL, double realizedPNL, string accountName) { }
        public void updateAccountTime(string timeStamp) { }
        public void accountDownloadEnd(string accountName) { }
        public void accountSummary(int reqId, string account, string tag, string value, string currency) { }
        public void accountSummaryEnd(int reqId) { }
        public void nextValidId(int orderId) { }
        public void bondContractDetails(int reqId, ContractDetails contractDetails) { }
        public void execDetails(int reqId, Contract contract, Execution execution) { }
        public void execDetailsEnd(int reqId) { }
        public void commissionReport(CommissionReport commissionReport) { }
        public void fundamentalData(int reqId, string data) { }
        public void historicalData(int reqId, Bar bar) { }
        public void historicalDataEnd(int reqId, string startDateStr, string endDateStr) { }
        public void marketDataType(int reqId, int marketDataType) { }
        public void updateMktDepth(int tickerId, int position, int operation, int side, double price, int size) { }
        public void updateMktDepthL2(int tickerId, int position, string marketMaker, int operation, int side, double price, int size, bool isSmartDepth) { }
        public void updateNewsBulletin(int newsMsgId, int newsMsgType, string newsMessage, string originExch) { }
        public void managedAccounts(string accountsList) { }
        public void receiveFA(int faDataType, string faXmlData) { }
        public void scannerParameters(string xml) { }
        public void scannerData(int reqId, int rank, ContractDetails contractDetails, string distance, string benchmark, string projection, string legsStr) { }
        public void scannerDataEnd(int reqId) { }
        public void realtimeBar(int reqId, long time, double open, double high, double low, double close, long volume, double wap, int count) { }
        public void deltaNeutralValidation(int reqId, DeltaNeutralContract deltaNeutralContract) { }
        public void tickSnapshotEnd(int tickerId) { }
        public void marketRule(int marketRuleId, PriceIncrement[] priceIncrements) { }
        public void pnl(int reqId, double dailyPnL, double unrealizedPnL, double realizedPnL) { }
        public void pnlSingle(int reqId, int pos, double dailyPnL, double unrealizedPnL, double realizedPnL, double value) { }
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
        public void tickReqParams(int tickerId, double minTick, string bboExchange, int snapshotPermissions) { }
        public void securityDefinitionOptionParameter(int reqId, string exchange, int underlyingConId, string tradingClass, string multiplier, HashSet<string> expirations, HashSet<double> strikes) { }
        public void securityDefinitionOptionParameterEnd(int reqId) { }
        public void softDollarTiers(int reqId, SoftDollarTier[] tiers) { }
        public void familyCodes(FamilyCode[] familyCodes) { }
        public void symbolSamples(int reqId, ContractDescription[] contractDescriptions) { }
        public void mktDepthExchanges(DepthMktDataDescription[] depthMktDataDescriptions) { }
        public void tickNews(int tickerId, long timeStamp, string providerCode, string articleId, string headline, string extraData) { }
        public void smartComponents(int reqId, Dictionary<int, KeyValuePair<string, char>> theMap) { }
        public void newsProviders(NewsProvider[] newsProviders) { }
        public void newsArticle(int requestId, int articleType, string articleText) { }
        public void historicalNews(int requestId, string time, string providerCode, string articleId, string headline) { }
        public void historicalNewsEnd(int requestId, bool hasMore) { }
        public void wshMetaData(int reqId, string dataJson) { }
        public void wshEventData(int reqId, string dataJson) { }
        public void historicalDataUpdate(int reqId, Bar bar) { }
        public void completedOrder(Contract contract, Order order, OrderState orderState) { }
        public void completedOrdersEnd() { }
        public void replaceFAEnd(int reqId, string text) { }
        public void displayGroupList(int reqId, string groups) { }
        public void displayGroupUpdated(int reqId, string contractInfo) { }
        public void verifyMessageAPI(string apiData) { }
        public void verifyCompleted(bool isSuccessful, string errorText) { }
        public void verifyAndAuthMessageAPI(string apiData, string xyzChallange) { }
        public void verifyAndAuthCompleted(bool isSuccessful, string errorText) { }
        public void connectAck() { }
        public void userInfo(int reqId, string whiteBrandingId) { }
        public void connectionClosed() { }
        public void position(string account, Contract contract, double pos, double avgCost) { }
        public void positionEnd() { }
        public void positionMulti(int reqId, string account, string modelCode, Contract contract, double pos, double avgCost) { }
        public void positionMultiEnd(int reqId) { }
        public void accountUpdateMulti(int reqId, string account, string modelCode, string key, string value, string currency) { }
        public void accountUpdateMultiEnd(int reqId) { }
        public void orderBound(long orderId, int apiClientId, int apiOrderId) { }
    }
}
