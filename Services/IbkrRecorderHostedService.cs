using System.Collections.Concurrent;
using System.Text.Json;
using IBApi;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace RamStockAlerts.Services;

/// <summary>
/// IBKR Recorder-only background service.
/// Connects to TWS/Gateway, subscribes to Level II depth + tape, records to JSONL.
/// No trading, no strategy execution—purely data collection.
/// Reads configuration from "Ibkr" section in appsettings.
/// 
/// ===== QUICK START =====
/// 1. Ensure TWS is running LIVE on 127.0.0.1:7496 with API enabled (Read-only: ON)
/// 2. Set Ibkr:Mode to "Record" in appsettings.Development.json
/// 3. Run: dotnet run
/// 4. Verify logs/ibkr-depth-AAPL-YYYYMMDD.jsonl and logs/ibkr-tape-AAPL-YYYYMMDD.jsonl grow for ~10 minutes
/// 
/// See IBKR_RECORDER_GUIDE.md for detailed setup and troubleshooting.
/// </summary>
public sealed class IbkrRecorderHostedService : BackgroundService, EWrapper
{
    private readonly ILogger<IbkrRecorderHostedService> _logger;
    private readonly IConfiguration _config;

    private EClientSocket? _client;
    private EReaderSignal? _signal;
    private EReader? _reader;

    private readonly ConcurrentQueue<string> _depthQueue = new();
    private readonly ConcurrentQueue<string> _tapeQueue = new();

    private long _depthCount;
    private long _tapeCount;

    // Configuration fields
    private string _host = "127.0.0.1";
    private int _port = 7496;
    private int _clientId = 12;
    private string _symbol = "AAPL";
    private int _depthRows = 10;
    private int _recordMinutes = 10;
    private string _depthExchange = "NASDAQ";
    private bool _useSmartDepth = false;

    // Request IDs (stable)
    private const int ReqIdDepth = 2001;
    private const int ReqIdTapeAllLast = 3001;
    private const int ReqIdMarketDataFallback = 3002;

    // Contract conId (resolved on startup)
    private int _conId;
    private bool _contractResolved;

    // File writers (rotate by day)
    private StreamWriter? _depthWriter;
    private StreamWriter? _tapeWriter;
    private DateOnly _currentDay;

    // Fallback tracking (tape)
    private bool _useFallback;
    private bool _fallbackAttempted;
    private double? _lastPrice;
    private int? _lastSize;

    // Depth fallback tracking
    private List<string> _depthExchanges = new();
    private int _depthExchangeIndex = 0;
    private int _depthAttemptCount = 0;
    private const int MaxDepthRetries = 3;

    private readonly JsonSerializerOptions _jsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = false };

    // Connection/subscription control flags
    private volatile bool _connected;
    private volatile bool _connectedLogEmitted;
    private volatile bool _subscriptionsStarted;
    private volatile bool _everConnected;
    private volatile bool _connectionClosedLogged;
    private readonly CancellationTokenSource _quitCts = new();

    public IbkrRecorderHostedService(ILogger<IbkrRecorderHostedService> logger, IConfiguration config)
    {
        _logger = logger;
        _config = config;
    }

    public override Task StartAsync(CancellationToken cancellationToken)
    {
        // Read IBKR configuration from appsettings
        var ibkrSection = _config.GetSection("Ibkr");
        _host = ibkrSection["Host"] ?? _host;
        _port = int.TryParse(ibkrSection["Port"], out var p) ? p : _port;
        _clientId = int.TryParse(ibkrSection["ClientId"], out var c) ? c : _clientId;
        _symbol = ibkrSection["Symbol"] ?? _symbol;
        _depthRows = int.TryParse(ibkrSection["DepthRows"], out var d) ? d : _depthRows;
        _recordMinutes = int.TryParse(ibkrSection["RecordMinutes"], out var m) ? m : _recordMinutes;
        _depthExchange = ibkrSection["DepthExchange"] ?? _depthExchange;
        _useSmartDepth = bool.TryParse(ibkrSection["UseSmartDepth"], out var usd) ? usd : _useSmartDepth;

        // Build depth exchange fallback list
        _depthExchanges = new List<string> { _depthExchange, "ISLAND", "NASDAQ", "ARCA" }
            .Distinct()
            .ToList();

        _logger.LogInformation(
            "[IBKR Recorder] Starting recorder | Host={Host} Port={Port} ClientId={ClientId} Symbol={Symbol} DepthRows={DepthRows} RecordMinutes={RecordMinutes}",
            _host, _port, _clientId, _symbol, _depthRows, _recordMinutes);
        _logger.LogInformation(
            "[IBKR Recorder] Config: Host={Host} Port={Port} ClientId={ClientId} Symbol={Symbol} DepthRows={DepthRows} RecordMinutes={RecordMinutes}",
            _host, _port, _clientId, _symbol, _depthRows, _recordMinutes);

        // Ensure logs directory
        Directory.CreateDirectory("logs");

        // Initialize file writers for current UTC day
        _currentDay = DateOnly.FromDateTime(DateTime.UtcNow);
        OpenWritersForDay(_currentDay);

        // Create socket and signal
        _signal = new EReaderMonitorSignal();
        _client = new EClientSocket(this, _signal);

        _logger.LogInformation("[IBKR Recorder] Connecting to {Host}:{Port} clientId={ClientId}...", _host, _port, _clientId);
        _client.eConnect(_host, _port, _clientId);

        if (!_client.IsConnected())
        {
            _logger.LogError("[IBKR Recorder] Failed to connect to {Host}:{Port}", _host, _port);
            return Task.CompletedTask;
        }

        _logger.LogInformation("[IBKR Recorder] Connected, starting EReader...");

        // Start EReader loop on background thread
        _reader = new EReader(_client, _signal);
        _reader.Start();

        new Thread(() =>
        {
            while (_client?.IsConnected() ?? false)
            {
                try
                {
                    _signal?.waitForSignal();
                    _reader?.processMsgs();
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "[IBKR Recorder] Error in EReader loop");
                    Thread.Sleep(100);
                }
            }

            // If we reach here, socket disconnected. If we had connected before, trigger shutdown.
            if (_everConnected && !_connectionClosedLogged)
            {
                _connectionClosedLogged = true;
                _logger.LogWarning("[IBKR Recorder] Socket disconnected after being connected. Exiting recorder.");
                try { _quitCts.Cancel(); } catch { }
            }
        })
        {
            IsBackground = true,
            Name = "IBKR-EReader-Loop"
        }.Start();

        return base.StartAsync(cancellationToken);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var startTime = DateTime.UtcNow;
        var stopTime = startTime.AddMinutes(_recordMinutes);

        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken, _quitCts.Token);
        var runToken = linkedCts.Token;

        _logger.LogInformation("[IBKR Recorder] Recording started, will stop at {StopTime:u}", stopTime);

        // Wait a moment for connect; nextValidId will trigger subscriptions when ready.
        await Task.Delay(500, runToken);

        // Main loop: drain queues and log counters every second
        while (!runToken.IsCancellationRequested && DateTime.UtcNow < stopTime)
        {
            RotateIfNewDay();
            DrainQueuesToDisk();

            _logger.LogInformation(
                "[IBKR Recorder] depthUpdates={DepthCount} tapePrints={TapeCount} depthQueue={DepthQueueSize} tapeQueue={TapeQueueSize}",
                _depthCount, _tapeCount, _depthQueue.Count, _tapeQueue.Count);

            try
            {
                await Task.Delay(TimeSpan.FromSeconds(1), runToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }

        _logger.LogInformation("[IBKR Recorder] Recording duration expired, shutting down...");

        // Final drain
        DrainQueuesToDisk();

        // Unsubscribe
        if (_client?.IsConnected() ?? false)
        {
            try
            {
                _client.cancelMktDepth(ReqIdDepth, false);
                _logger.LogInformation("[IBKR Recorder] Cancelled market depth.");
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[IBKR Recorder] Error cancelling market depth.");
            }

            try
            {
                _client.cancelTickByTickData(ReqIdTapeAllLast);
                _logger.LogInformation("[IBKR Recorder] Cancelled tick-by-tick (AllLast).");
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[IBKR Recorder] Error cancelling tick-by-tick.");
            }

            if (_useFallback)
            {
                try
                {
                    _client.cancelMktData(ReqIdMarketDataFallback);
                    _logger.LogInformation("[IBKR Recorder] Cancelled market data (fallback).");
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "[IBKR Recorder] Error cancelling market data fallback.");
                }
            }

            try
            {
                _client.eDisconnect();
                _logger.LogInformation("[IBKR Recorder] Disconnected.");
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[IBKR Recorder] Error during disconnect.");
            }
        }

        _logger.LogInformation("[IBKR Recorder] Shutdown complete. Files in logs/: ibkr-depth-{Sym1}-*.jsonl, ibkr-tape-{Sym2}-*.jsonl", _symbol, _symbol);
    }

    public override Task StopAsync(CancellationToken cancellationToken)
    {
        try
        {
            _depthWriter?.Dispose();
            _tapeWriter?.Dispose();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[IBKR Recorder] Error disposing writers.");
        }

        return base.StopAsync(cancellationToken);
    }

    // ============================================================================
    // IBKR Requests
    // ============================================================================

    private void RequestContractDetails()
    {
        if (_client?.IsConnected() != true)
        {
            _logger.LogWarning("[IBKR Recorder] Cannot request contract details; not connected.");
            return;
        }

        var contract = new Contract
        {
            Symbol = _symbol,
            SecType = "STK",
            Exchange = "SMART",
            Currency = "USD"
        };

        const int reqIdContractDetails = 1001;
        _client.reqContractDetails(reqIdContractDetails, contract);
        _logger.LogInformation("[IBKR Recorder] Requested contract details for {Symbol}.", _symbol);
    }

    private void SubscribeDepth()
    {
        if (_client?.IsConnected() != true)
        {
            _logger.LogWarning("[IBKR Recorder] Cannot subscribe to depth; not connected.");
            return;
        }

        if (_depthExchangeIndex >= _depthExchanges.Count)
        {
            _logger.LogWarning("[IBKR Recorder] Depth not available after retries. Recording tape only.");
            return;
        }

        var exchange = _depthExchanges[_depthExchangeIndex];
        _depthAttemptCount++;

        var contract = new Contract
        {
            Symbol = _symbol,
            SecType = "STK",
            Exchange = exchange,
            Currency = "USD"
        };

        _client.reqMarketDepth(ReqIdDepth, contract, _depthRows, _useSmartDepth, null);
        _logger.LogInformation("[IBKR Recorder] Depth subscribe attempt {Attempt}: Exchange={Exchange}", _depthAttemptCount, exchange);
    }

    private void SubscribeTape()
    {
        if (_client?.IsConnected() != true)
        {
            _logger.LogWarning("[IBKR Recorder] Cannot subscribe to tape; not connected.");
            return;
        }

        var contract = new Contract
        {
            Symbol = _symbol,
            SecType = "STK",
            Exchange = "SMART",
            Currency = "USD"
        };

        // Attempt tick-by-tick AllLast
        _client.reqTickByTickData(ReqIdTapeAllLast, contract, "AllLast", 0, false);
        _logger.LogInformation("[IBKR Recorder] Subscribed to tick-by-tick AllLast (reqId={ReqId}).", ReqIdTapeAllLast);
    }

    private void SubscribeTapeFallback()
    {
        if (_useFallback || _fallbackAttempted || _client?.IsConnected() != true)
        {
            _logger.LogWarning("[IBKR Recorder] Fallback already attempted or not connected.");
            return;
        }

        _fallbackAttempted = true;

        var contract = new Contract
        {
            Symbol = _symbol,
            SecType = "STK",
            Exchange = "SMART",
            Currency = "USD"
        };

        _client.reqMktData(ReqIdMarketDataFallback, contract, "", false, false, null);
        _useFallback = true;
        _logger.LogInformation("[IBKR Recorder] Fallback: subscribed to market data (reqId={ReqId}) for tape simulation.", ReqIdMarketDataFallback);
    }

    // ============================================================================
    // File I/O
    // ============================================================================

    private void RotateIfNewDay()
    {
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        if (today != _currentDay)
        {
            _currentDay = today;
            OpenWritersForDay(today);
        }
    }

    private void OpenWritersForDay(DateOnly day)
    {
        _depthWriter?.Dispose();
        _tapeWriter?.Dispose();

        var dateStr = day.ToString("yyyyMMdd");
        var depthPath = Path.Combine("logs", $"ibkr-depth-{_symbol}-{dateStr}.jsonl");
        var tapePath = Path.Combine("logs", $"ibkr-tape-{_symbol}-{dateStr}.jsonl");

        _depthWriter = new StreamWriter(new FileStream(depthPath, FileMode.Append, FileAccess.Write, FileShare.Read))
        {
            AutoFlush = true
        };

        _tapeWriter = new StreamWriter(new FileStream(tapePath, FileMode.Append, FileAccess.Write, FileShare.Read))
        {
            AutoFlush = true
        };

        _logger.LogInformation("[IBKR Recorder] Opened file writers: {DepthPath} and {TapePath}", depthPath, tapePath);
    }

    private void DrainQueuesToDisk()
    {
        int depthCount = 0;
        while (depthCount < 100_000 && _depthQueue.TryDequeue(out var depthLine))
        {
            _depthWriter?.WriteLine(depthLine);
            depthCount++;
        }

        int tapeCount = 0;
        while (tapeCount < 100_000 && _tapeQueue.TryDequeue(out var tapeLine))
        {
            _tapeWriter?.WriteLine(tapeLine);
            tapeCount++;
        }
    }

    private string ToJsonLine(object obj)
    {
        return JsonSerializer.Serialize(obj, _jsonOptions);
    }

    // ============================================================================
    // EWrapper: Events
    // ============================================================================

    public void nextValidId(int orderId)
    {
        if (!_connectedLogEmitted)
        {
            _connectedLogEmitted = true;
            _logger.LogInformation("[IBKR Recorder] Connected to TWS. nextValidId={orderId}", orderId);
        }

        _connected = true;
        _everConnected = true;

        // Kick off one-time setup: contract details then subscriptions
        if (!_subscriptionsStarted)
        {
            _subscriptionsStarted = true;

            // Request contract details
            RequestContractDetails();

            // Fire-and-forget waiter to start subscriptions once contract details resolve or timeout
            _ = Task.Run(async () =>
            {
                var until = DateTime.UtcNow.AddSeconds(10);
                while (!_contractResolved && DateTime.UtcNow < until && !(_quitCts.IsCancellationRequested))
                {
                    await Task.Delay(100);
                }

                if (!_contractResolved)
                {
                    _logger.LogWarning("[IBKR Recorder] Contract resolution timed out; proceeding without conId.");
                }

                SubscribeDepth();
                SubscribeTape();
                _logger.LogInformation("[IBKR Recorder] Subscriptions sent. Recording to logs/");
            });
        }
    }

    public void error(int id, int errorCode, string errorMsg)
    {
        if (errorCode == 2104 || errorCode == 2106)
        {
            // Market data farm connection is OK (informational)
            _logger.LogInformation("[IBKR Recorder] Info: id={Id} code={Code} msg={Msg}", id, errorCode, errorMsg);
            return;
        }

        _logger.LogWarning("[IBKR Recorder] IBKR error: id={Id} code={Code} msg={Msg}", id, errorCode, errorMsg);

        // Check if depth request failed with error 10092 (depth not available)
        if (id == ReqIdDepth && errorCode == 10092 && _depthAttemptCount < MaxDepthRetries)
        {
            _logger.LogInformation(
                "[IBKR Recorder] Depth unavailable on exchange (error {ErrorCode}); retrying with next exchange.",
                errorCode);

            // Cancel current depth subscription if needed
            try
            {
                _client?.cancelMktDepth(ReqIdDepth, false);
            }
            catch { }

            // Move to next exchange and retry
            _depthExchangeIndex++;
            SubscribeDepth();
            return;
        }

        // Check if tick-by-tick request failed
        if (id == ReqIdTapeAllLast && errorCode >= 10000 && !_fallbackAttempted)
        {
            _logger.LogInformation(
                "[IBKR Recorder] Tick-by-tick not available (error {ErrorCode}); attempting fallback to market data.",
                errorCode);
            SubscribeTapeFallback();
        }
    }

    public void error(string str)
    {
        _logger.LogError("[IBKR Recorder] IBKR error (string): {Message}", str);
    }

    public void error(Exception e)
    {
        _logger.LogError(e, "[IBKR Recorder] IBKR error (exception)");
    }

    public void contractDetails(int reqId, ContractDetails contractDetails)
    {
        if (reqId == 1001 && contractDetails.Contract != null)
        {
            _conId = contractDetails.Contract.ConId;
            _contractResolved = _conId != 0;
            _logger.LogInformation(
                "[IBKR Recorder] Contract resolved: {Symbol} conId={ConId} primaryExch={PrimaryExch}",
                _symbol, _conId, contractDetails.Contract.PrimaryExch);
        }
    }

    public void contractDetailsEnd(int reqId)
    {
        if (reqId == 1001)
        {
            _logger.LogInformation("[IBKR Recorder] contractDetailsEnd");
        }
    }

    public void updateMktDepth(int tickerId, int position, int operation, int side, double price, int size)
    {
        Interlocked.Increment(ref _depthCount);

        var sideStr = side switch
        {
            0 => "Ask",
            1 => "Bid",
            _ => "Unknown"
        };

        var evt = new
        {
            timestampUtc = DateTime.UtcNow.ToString("o"),
            symbol = _symbol,
            eventType = "Depth",
            side = sideStr,
            position,
            operation,
            price,
            size
        };

        _depthQueue.Enqueue(ToJsonLine(evt));
    }

    public void updateMktDepthL2(int tickerId, int position, string marketMaker, int operation, int side, double price, int size, bool isSmartDepth)
    {
        Interlocked.Increment(ref _depthCount);

        var sideStr = side switch
        {
            0 => "Ask",
            1 => "Bid",
            _ => "Unknown"
        };

        var evt = new
        {
            timestampUtc = DateTime.UtcNow.ToString("o"),
            symbol = _symbol,
            eventType = "Depth",
            side = sideStr,
            position,
            marketMaker,
            operation,
            price,
            size,
            isSmartDepth
        };

        _depthQueue.Enqueue(ToJsonLine(evt));
    }

    public void tickByTickAllLast(int reqId, int tickType, long time, double price, int size,
        TickAttribLast tickAttribLast, string exchange, string specialConditions)
    {
        Interlocked.Increment(ref _tapeCount);

        var evt = new
        {
            timestampUtc = DateTime.UtcNow.ToString("o"),
            symbol = _symbol,
            eventType = "Tape",
            price,
            size,
            exchange,
            specialConditions
        };

        _tapeQueue.Enqueue(ToJsonLine(evt));
    }

    public void tickPrice(int tickerId, int field, double price, TickAttrib attribs)
    {
        // Field 4 = LAST price
        if (field == 4)
        {
            _lastPrice = price;
            EmitTapeIfReady();
        }
    }

    public void tickSize(int tickerId, int field, int size)
    {
        // Field 5 = LAST size
        if (field == 5)
        {
            _lastSize = size;
            EmitTapeIfReady();
        }
    }

    private void EmitTapeIfReady()
    {
        if (_useFallback && _lastPrice.HasValue && _lastSize.HasValue)
        {
            Interlocked.Increment(ref _tapeCount);

            var evt = new
            {
                timestampUtc = DateTime.UtcNow.ToString("o"),
                symbol = _symbol,
                eventType = "Tape",
                price = _lastPrice.Value,
                size = _lastSize.Value,
                exchange = (string?)null,
                specialConditions = (string?)null
            };

            _tapeQueue.Enqueue(ToJsonLine(evt));

            // Reset for next tick
            _lastPrice = null;
            _lastSize = null;
        }
    }

    // ============================================================================
    // EWrapper: Stubs (no-op)
    // ============================================================================

    public void tickOptionComputation(int tickerId, int field, double impliedVol, double delta, double optPrice, double pvDividend, double gamma, double vega, double theta, double undPrice) { }
    public void tickGeneric(int tickerId, int field, double value) { }
    public void tickString(int tickerId, int field, string value) { }
    public void tickEFP(int tickerId, int tickType, double basisPoints, string formattedBasisPoints, double impliedFuture, int holdDays, string futureExpiry, double dividendImpact, double dividendsToExpiry) { }
    public void orderStatus(int orderId, string status, double filled, double remaining, double avgFillPrice, int permId, int parentId, double lastFillPrice, int clientId, string whyHeld, double mktCapPrice) { }
    public void openOrder(int orderId, Contract contract, Order order, OrderState orderState) { }
    public void openOrderEnd() { }
    public void updateAccountValue(string key, string value, string currency, string accountName) { }
    public void updatePortfolio(Contract contract, double position, double marketPrice, double marketValue, double averageCost, double unrealizedPNL, double realizedPNL, string accountName) { }
    public void updateAccountTime(string timeStamp) { }
    public void accountDownloadEnd(string account) { }
    public void bondContractDetails(int reqId, ContractDetails contractDetails) { }
    public void execDetails(int reqId, Contract contract, IBApi.Execution execution) { }
    public void execDetailsEnd(int reqId) { }
    public void updateNewsBulletin(int msgId, int msgType, string message, string origExchange) { }
    public void managedAccounts(string accountsList) { }
    public void receiveFA(int faDataType, string faXmlData) { }
    public void historicalData(int reqId, Bar bar) { }
    public void scannerParameters(string xml) { }
    public void scannerData(int reqId, int rank, ContractDetails contractDetails, string distance, string benchmark, string projection, string legsStr) { }
    public void scannerDataEnd(int reqId) { }
    public void realtimeBar(int reqId, long time, double open, double high, double low, double close, long volume, double wap, int count) { }
    public void currentTime(long time) { }
    public void fundamentalData(int reqId, string data) { }
    public void deltaNeutralValidation(int reqId, DeltaNeutralContract deltaNeutralContract) { }
    public void tickSnapshotEnd(int reqId) { }
    public void marketDataType(int reqId, int marketDataType) { }
    public void commissionReport(CommissionReport commissionReport) { }
    public void position(string account, Contract contract, double pos, double avgCost) { }
    public void positionEnd() { }
    public void accountSummary(int reqId, string account, string tag, string value, string currency) { }
    public void accountSummaryEnd(int reqId) { }
    public void verifyMessageAPI(string apiData) { }
    public void verifyCompleted(bool isSuccessful, string errorText) { }
    public void verifyAndAuthMessageAPI(string apiData, string xyzChallenge) { }
    public void verifyAndAuthCompleted(bool isSuccessful, string errorText) { }
    public void displayGroupList(int reqId, string groups) { }
    public void displayGroupUpdated(int reqId, string contractInfo) { }
    public void connectAck() { }
    public void positionMulti(int reqId, string account, string modelCode, Contract contract, double pos, double avgCost) { }
    public void positionMultiEnd(int reqId) { }
    public void accountUpdateMulti(int reqId, string account, string modelCode, string key, string value, string currency) { }
    public void accountUpdateMultiEnd(int reqId) { }
    public void securityDefinitionOptionalParameter(int reqId, string exchange, int underlyingConId, string tradingClass, string multiplier, HashSet<string> expirations, HashSet<double> strikes) { }
    public void securityDefinitionOptionalParameterEnd(int reqId) { }
    public void softDollarTiers(int reqId, SoftDollarTier[] tiers) { }
    public void familyCodes(FamilyCode[] familyCodes) { }
    public void symbolSamples(int reqId, ContractDescription[] contractDescriptions) { }
    public void mktDepthExchanges(DepthMktDataDescription[] depthMktDataDescriptions) { }
    public void tickNews(int tickerId, long timeStamp, string providerCode, string articleId, string headline, string extraData) { }
    public void smartComponents(int reqId, Dictionary<int, KeyValuePair<string, char>> theMap) { }
    public void tickReqParams(int tickerId, double minTick, string bboExchange, int snapshotPermissions) { }
    public void newsProviders(NewsProvider[] newsProviders) { }
    public void newsArticle(int requestId, int articleType, string articleText) { }
    public void historicalNews(int requestId, string time, string providerCode, string articleId, string headline) { }
    public void historicalNewsEnd(int requestId, bool hasMore) { }
    public void headTimestamp(int reqId, string headTimestamp) { }
    public void histogramData(int reqId, HistogramEntry[] data) { }
    public void rerouteMktDataReq(int reqId, int conId, string exchange) { }
    public void rerouteMktDepthReq(int reqId, int conId, string exchange) { }
    public void marketRule(int marketRuleId, PriceIncrement[] priceIncrements) { }
    public void pnl(int reqId, double dailyPnL, double unrealizedPnL, double realizedPnL) { }
    public void pnlSingle(int reqId, int pos, double dailyPnL, double unrealizedPnL, double realizedPnL, double value) { }
    public void historicalTicks(int reqId, HistoricalTick[] ticks, bool done) { }
    public void historicalTicksBidAsk(int reqId, HistoricalTickBidAsk[] ticks, bool done) { }
    public void historicalTicksLast(int reqId, HistoricalTickLast[] ticks, bool done) { }
    public void tickByTickBidAsk(int reqId, long time, double bidPrice, double askPrice, int bidSize, int askSize, TickAttribBidAsk tickAttribBidAsk) { }
    public void tickByTickMidPoint(int reqId, long time, double midPoint) { }
    public void orderBound(long orderId, int apiClientId, int apiOrderId) { }
    public void completedOrder(Contract contract, Order order, OrderState orderState) { }
    public void completedOrdersEnd() { }
    public void replaceFAEnd(int reqId, string text) { }
    public void wshMetaData(int reqId, string dataJson) { }
    public void wshEventData(int reqId, string dataJson) { }
    public void connectionClosed()
    {
        if (_everConnected && !_connectionClosedLogged)
        {
            _connectionClosedLogged = true;
            _logger.LogWarning("[IBKR Recorder] Connection closed by TWS/Gateway after being connected. Exiting.");
            try { _quitCts.Cancel(); } catch { }
        }
        else
        {
            _logger.LogWarning("[IBKR Recorder] Connection closed.");
        }
    }
    public void historicalDataEnd(int reqId, string startDateStr, string endDateStr) { }
    public void historicalDataUpdate(int reqId, Bar bar) { }
    public void securityDefinitionOptionParameter(int reqId, string exchange, int underlyingConId, string tradingClass, string multiplier, HashSet<string> expirations, HashSet<double> strikes) { }
    public void securityDefinitionOptionParameterEnd(int reqId) { }
    public void userInfo(int reqId, string whiteBrandingId) { }


}
