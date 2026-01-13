using System;
using IBApi;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using RamStockAlerts.Services.Universe;

namespace RamStockAlerts.Universe;

public sealed class IbkrScannerUniverseSource : IUniverseSource
{
    private readonly IConfiguration _configuration;
    private readonly ILogger<IbkrScannerUniverseSource> _logger;
    private readonly ContractClassificationService _classificationService;
    private static int _nextRequestId = 5000;
    private readonly object _cacheLock = new();
    private IReadOnlyList<string> _lastUniverse = Array.Empty<string>();
    private readonly int _startHourEt;
    private readonly int _startMinuteEt;
    private readonly int _endHourEt;
    private readonly int _endMinuteEt;
    private readonly TimeZoneInfo _eastern;

    public IbkrScannerUniverseSource(
        IConfiguration configuration,
        ILogger<IbkrScannerUniverseSource> logger,
        ContractClassificationService classificationService)
    {
        _startHourEt = Math.Clamp(configuration.GetValue("Universe:IbkrScanner:StartHour", 7), 0, 23);
        _startMinuteEt = Math.Clamp(configuration.GetValue("Universe:IbkrScanner:StartMinute", 0), 0, 59);
        _endHourEt = Math.Clamp(configuration.GetValue("Universe:IbkrScanner:EndHour", 16), 0, 23);
        _endMinuteEt = Math.Clamp(configuration.GetValue("Universe:IbkrScanner:EndMinute", 0), 0, 59);
        _eastern = TryGetEasternTimeZone();

        _configuration = configuration;
        _logger = logger;
        _classificationService = classificationService ?? throw new ArgumentNullException(nameof(classificationService));
    }

    public async Task<IReadOnlyList<string>> GetUniverseAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (!IsWithinOperatingWindow())
        {
            var cached = GetCachedUniverse();
            _logger.LogInformation(
                "[IBKR Scanner] Skipping universe refresh outside window {StartHour:D2}:{StartMinute:D2}-{EndHour:D2}:{EndMinute:D2} ET. Returning cached count={Count}.",
                _startHourEt,
                _startMinuteEt,
                _endHourEt,
                _endMinuteEt,
                cached.Count);
            return cached;
        }

        var host = _configuration["IBKR:Host"] ?? _configuration["Ibkr:Host"] ?? "127.0.0.1";
        var port = _configuration.GetValue<int?>("IBKR:Port")
                   ?? _configuration.GetValue<int?>("Ibkr:Port")
                   ?? 7496;
        var baseClientId = _configuration.GetValue<int?>("IBKR:ClientId")
                         ?? _configuration.GetValue<int?>("Ibkr:ClientId")
                         ?? 1;
        var clientId = _configuration.GetValue<int?>("Universe:IbkrScanner:ClientId") ?? baseClientId + 1;

        var instrument = _configuration["Universe:IbkrScanner:Instrument"] ?? "STK";
        var locationCode = _configuration["Universe:IbkrScanner:LocationCode"] ?? "STK.US.MAJOR";
        var scanCode = _configuration["Universe:IbkrScanner:ScanCode"] ?? "MOST_ACTIVE";
        var rows = Math.Clamp(_configuration.GetValue("Universe:IbkrScanner:Rows", 50), 1, 50);
        var abovePrice = _configuration.GetValue("Universe:IbkrScanner:AbovePrice", 5d);
        var belowPrice = _configuration.GetValue("Universe:IbkrScanner:BelowPrice", 50d);
        var aboveVolume = _configuration.GetValue("Universe:IbkrScanner:AboveVolume", 500_000);

        var maxRetries = Math.Max(1, _configuration.GetValue("Universe:Scanner:MaxRetries", 3));
        var backoffs = new[] { TimeSpan.Zero, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(5) };
        Exception? lastException = null;

        for (var attempt = 1; attempt <= maxRetries; attempt++)
        {
            if (attempt > 1)
            {
                var delay = attempt - 1 < backoffs.Length ? backoffs[attempt - 1] : backoffs[^1];
                if (delay > TimeSpan.Zero)
                {
                    await Task.Delay(delay, cancellationToken);
                }
            }

            _logger.LogInformation("[IBKR Scanner] Attempt {Attempt}/{MaxRetries}", attempt, maxRetries);

            try
            {
                var universe = await ExecuteScannerAttemptAsync(
                    host,
                    port,
                    clientId,
                    instrument,
                    locationCode,
                    scanCode,
                    rows,
                    abovePrice,
                    belowPrice,
                    aboveVolume,
                    cancellationToken);

                if (universe.Count > 0)
                {
                    lock (_cacheLock)
                    {
                        _lastUniverse = universe;
                    }
                }

                LogUniverse(universe);
                return universe;
            }
            catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
            {
                lastException = ex;
                _logger.LogWarning(ex, "[IBKR Scanner] Attempt {Attempt}/{MaxRetries} failed.", attempt, maxRetries);
            }
        }

        throw lastException ?? new InvalidOperationException("IBKR scanner request failed.");
    }

    private async Task<IReadOnlyList<string>> ExecuteScannerAttemptAsync(
        string host,
        int port,
        int clientId,
        string instrument,
        string locationCode,
        string scanCode,
        int rows,
        double abovePrice,
        double belowPrice,
        int aboveVolume,
        CancellationToken cancellationToken)
    {
        var requestId = Interlocked.Increment(ref _nextRequestId);
        var symbols = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var candidates = new List<ScannerCandidate>();
        var completion = new TaskCompletionSource<IReadOnlyList<string>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var wrapper = new ScannerWrapper(
            _logger,
            _classificationService,
            requestId,
            symbols,
            candidates,
            completion);

        var readerSignal = new EReaderMonitorSignal();
        var client = new EClientSocket(wrapper, readerSignal);

        try
        {
            _logger.LogInformation(
                "[IBKR Scanner] Connecting to {Host}:{Port} clientId={ClientId}",
                host,
                port,
                clientId);

            client.eConnect(host, port, clientId);

            if (!client.IsConnected())
            {
                throw new InvalidOperationException($"IBKR scanner failed to connect to {host}:{port}.");
            }

            client.startApi();

            using var cancellationRegistration = cancellationToken.Register(() =>
            {
                try
                {
                    readerSignal.issueSignal();
                }
                catch
                {
                }

                completion.TrySetCanceled(cancellationToken);
            });

            _ = Task.Run(() => ProcessMessages(client, readerSignal, completion, _logger, cancellationToken), cancellationToken);

            var subscription = new ScannerSubscription
            {
                Instrument = instrument,
                LocationCode = locationCode,
                ScanCode = scanCode,
                NumberOfRows = rows,
                AbovePrice = abovePrice,
                BelowPrice = belowPrice,
                AboveVolume = aboveVolume,
                StockTypeFilter = "CS"
            };

            _logger.LogInformation(
                "[IBKR Scanner] Requesting scan {ScanCode} {Instrument} {Location} rows={Rows} price={AbovePrice}-{BelowPrice} volume>={AboveVolume}",
                scanCode,
                instrument,
                locationCode,
                rows,
                abovePrice,
                belowPrice,
                aboveVolume);

            client.reqScannerSubscription(
                requestId,
                subscription,
                new List<TagValue>(),
                new List<TagValue>());

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(10));

            var completed = await Task.WhenAny(
                completion.Task,
                Task.Delay(Timeout.Infinite, timeoutCts.Token));

            if (completed != completion.Task)
            {
                throw new TimeoutException("IBKR scanner request timed out.");
            }

            return await completion.Task;
        }
        finally
        {
            if (client.IsConnected())
            {
                try
                {
                    client.cancelScannerSubscription(requestId);
                }
                catch
                {
                }

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
        }
    }

    private IReadOnlyList<string> GetCachedUniverse()
    {
        lock (_cacheLock)
        {
            return _lastUniverse;
        }
    }

    private void LogUniverse(IReadOnlyList<string> universe)
    {
        var top = universe.Take(10).ToArray();
        var topDisplay = top.Length == 0 ? "n/a" : string.Join(", ", top);
        _logger.LogInformation(
            "Universe source=IbkrScanner count={Count} top10={Top}",
            universe.Count,
            topDisplay);
    }

    private static void ProcessMessages(
        EClientSocket socket,
        EReaderSignal signal,
        TaskCompletionSource<IReadOnlyList<string>> completion,
        ILogger logger,
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
            catch (Exception ex)
            {
                logger.LogWarning(ex, "[IBKR Scanner] Message processing failed; ending scanner session.");
                completion.TrySetException(ex);
                break;
            }
        }

        if (!stoppingToken.IsCancellationRequested && !completion.Task.IsCompleted)
        {
            completion.TrySetException(new InvalidOperationException("IBKR scanner stream ended unexpectedly."));
        }
    }

    private sealed class ScannerWrapper : EWrapper
    {
        private readonly ILogger _logger;
        private readonly ContractClassificationService _classificationService;
        private readonly int _requestId;
        private readonly HashSet<string> _symbols;
        private readonly List<ScannerCandidate> _candidates;
        private readonly TaskCompletionSource<IReadOnlyList<string>> _completion;

        public ScannerWrapper(
            ILogger logger,
            ContractClassificationService classificationService,
            int requestId,
            HashSet<string> symbols,
            List<ScannerCandidate> candidates,
            TaskCompletionSource<IReadOnlyList<string>> completion)
        {
            _logger = logger;
            _classificationService = classificationService;
            _requestId = requestId;
            _symbols = symbols;
            _candidates = candidates;
            _completion = completion;
        }

        public void scannerData(int reqId, int rank, ContractDetails contractDetails, string distance, string benchmark, string projection, string legsStr)
        {
            if (reqId != _requestId)
            {
                return;
            }

            var symbol = contractDetails?.Contract?.Symbol;
            if (string.IsNullOrWhiteSpace(symbol))
            {
                return;
            }

            lock (_symbols)
            {
                var normalized = symbol.Trim().ToUpperInvariant();
                if (_symbols.Add(normalized))
                {
                    _candidates.Add(new ScannerCandidate(rank, normalized, contractDetails));
                }
            }
        }

        public void scannerDataEnd(int reqId)
        {
            if (reqId != _requestId)
            {
                return;
            }

            List<string> result;
            lock (_symbols)
            {
                result = _candidates
                    .OrderBy(candidate => candidate.Rank)
                    .Select(candidate => candidate.Symbol)
                    .ToList();

                CacheClassifications();
            }

            _completion.TrySetResult(result);
        }

        public void connectionClosed()
        {
            _logger.LogWarning("[IBKR Scanner] Connection closed.");
            _completion.TrySetException(new InvalidOperationException("IBKR scanner connection closed."));
        }

        public void error(int id, int errorCode, string errorMsg)
        {
            if (errorCode == 2104 || errorCode == 2106 || errorCode == 2158)
            {
                _logger.LogInformation("[IBKR Scanner] Info: id={Id} code={Code} msg={Msg}", id, errorCode, errorMsg);
                return;
            }

            _logger.LogWarning("[IBKR Scanner] Error: id={Id} code={Code} msg={Msg}", id, errorCode, errorMsg);

            if (id == _requestId)
            {
                _completion.TrySetException(new InvalidOperationException($"IBKR scanner error {errorCode}: {errorMsg}"));
            }
        }

        public void error(string str) => _logger.LogError("[IBKR Scanner] {Message}", str);
        public void error(Exception e) => _logger.LogError(e, "[IBKR Scanner] Exception");

        public void nextValidId(int orderId) { }
        public void currentTime(long time) { }

        public void updateMktDepth(int tickerId, int position, int operation, int side, double price, int size) { }
        public void updateMktDepthL2(int tickerId, int position, string marketMaker, int operation, int side, double price, int size, bool isSmartDepth) { }
        public void tickByTickAllLast(int reqId, int tickType, long time, double price, int size, TickAttribLast tickAttribLast, string exchange, string specialConditions) { }
        public void tickByTickBidAsk(int reqId, long time, double bidPrice, double askPrice, int bidSize, int askSize, TickAttribBidAsk tickAttribBidAsk) { }
        public void tickByTickMidPoint(int reqId, long time, double midPoint) { }

        public void tickPrice(int tickerId, int field, double price, TickAttrib attribs) { }
        public void tickSize(int tickerId, int field, int size) { }
        public void tickOptionComputation(int tickerId, int field, double impliedVol, double delta, double optPrice, double pvDividend, double gamma, double vega, double theta, double undPrice) { }
        public void tickGeneric(int tickerId, int field, double value) { }
        public void tickString(int tickerId, int field, string value) { }
        public void tickEFP(int tickerId, int tickType, double basisPoints, string formattedBasisPoints, double impliedFuture, int holdDays, string futureExpiry, double dividendImpact, double dividendsToExpiry) { }
        public void tickSnapshotEnd(int tickerId) { }
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

        public void tickReqParams(int tickerId, double minTick, string bboExchange, int snapshotPermissions) { }
        public void tickByTick(int reqId, int tickType, long time, double price, int size, TickAttribLast tickAttribLast, string exchange, string specialConditions) { }

        public void userInfo(int reqId, string whiteBrandingId) { }

        private void CacheClassifications()
        {
            var tasks = new List<Task>();
            foreach (var candidate in _candidates)
            {
                if (candidate.Details is null)
                {
                    continue;
                }

                var classification = ToClassification(candidate.Details);
                tasks.Add(_classificationService.CacheAsync(classification, CancellationToken.None));
            }

            if (tasks.Count > 0)
            {
                Task.WhenAll(tasks).GetAwaiter().GetResult();
            }
        }

        private static ContractClassification ToClassification(ContractDetails details)
        {
            var now = DateTimeOffset.UtcNow;
            return new ContractClassification(
                details.Contract?.Symbol?.Trim().ToUpperInvariant() ?? string.Empty,
                details.Contract?.ConId ?? 0,
                details.Contract?.PrimaryExch,
                details.Contract?.Currency,
                ResolveStockType(details),
                now);
        }

        private static string ResolveStockType(ContractDetails details)
        {
            if (!string.IsNullOrWhiteSpace(details.StockType))
            {
                return details.StockType.Trim().ToUpperInvariant();
            }

            var secType = details.Contract?.SecType;
            if (!string.IsNullOrWhiteSpace(secType))
            {
                return secType.Trim().ToUpperInvariant() == "STK"
                    ? "COMMON"
                    : secType.Trim().ToUpperInvariant();
            }

            return "UNKNOWN";
        }
    }

    private sealed record ScannerCandidate(int Rank, string Symbol, ContractDetails? Details);

    private bool IsWithinOperatingWindow()
    {
        var nowEastern = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, _eastern);
        var minuteOfDay = nowEastern.Hour * 60 + nowEastern.Minute;
        var startMinute = _startHourEt * 60 + _startMinuteEt;
        var endMinute = _endHourEt * 60 + _endMinuteEt;

        return minuteOfDay >= startMinute && minuteOfDay < endMinute;
    }

    private static TimeZoneInfo TryGetEasternTimeZone()
    {
        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById("Eastern Standard Time");
        }
        catch (TimeZoneNotFoundException)
        {
            return TimeZoneInfo.Utc;
        }
    }
}
