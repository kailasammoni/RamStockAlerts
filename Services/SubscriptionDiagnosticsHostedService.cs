using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using IBApi;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using RamStockAlerts.Services.Universe;

namespace RamStockAlerts.Services;

/// <summary>
/// Diagnostics hosted service that tests subscription health for symbols on various exchanges.
/// Helps determine if symbols are dead due to: symbol not trading, exchange routing wrong,
/// entitlement missing, tick-by-tick not enabled, or mapping bugs.
/// </summary>
public sealed class SubscriptionDiagnosticsHostedService : IHostedService, IDisposable
{
    private readonly IConfiguration _configuration;
    private readonly ILogger<SubscriptionDiagnosticsHostedService> _logger;
    private readonly IHostApplicationLifetime _lifetime;
    private readonly ContractClassificationService _classificationService;
    private EClientSocket? _client;
    private DiagnosticEWrapper? _wrapper;
    private readonly List<DiagnosticResult> _results = new();
    private readonly ConcurrentDictionary<int, DiagnosticSession> _activeSessions = new();
    private const int TestDurationMs = 15_000;
    private int _nextRequestId = 10000;

    public SubscriptionDiagnosticsHostedService(
        IConfiguration configuration,
        ILogger<SubscriptionDiagnosticsHostedService> logger,
        IHostApplicationLifetime lifetime,
        ContractClassificationService classificationService)
    {
        _configuration = configuration;
        _logger = logger;
        _lifetime = lifetime;
        _classificationService = classificationService;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("[Diagnostics] Starting subscription diagnostics mode");

        // Read symbols from config
        var symbolsConfig = _configuration.GetValue<string>("Diagnostics:Symbols");
        if (string.IsNullOrWhiteSpace(symbolsConfig))
        {
            _logger.LogError("[Diagnostics] No symbols configured. Set Diagnostics:Symbols in config.");
            _lifetime.StopApplication();
            return;
        }

        var symbols = symbolsConfig.Split(',', StringSplitOptions.RemoveEmptyEntries)
            .Select(s => s.Trim())
            .Take(10)
            .ToList();

        if (symbols.Count == 0)
        {
            _logger.LogError("[Diagnostics] No valid symbols found in config");
            _lifetime.StopApplication();
            return;
        }

        _logger.LogInformation("[Diagnostics] Testing {Count} symbols: {Symbols}", 
            symbols.Count, string.Join(", ", symbols));

        // Create and connect IBKR client
        var host = _configuration.GetValue("Ibkr:Host", "127.0.0.1");
        var port = _configuration.GetValue("Ibkr:Port", 7497);
        var clientId = _configuration.GetValue("Ibkr:ClientId", 1);

        var signal = new EReaderMonitorSignal();
        _wrapper = new DiagnosticEWrapper(_logger, _activeSessions);
        _client = new EClientSocket(_wrapper, signal);

        _logger.LogInformation("[Diagnostics] Connecting to IBKR at {Host}:{Port} with clientId={ClientId}", 
            host, port, clientId);
        _client.eConnect(host, port, clientId);

        if (!_client.IsConnected())
        {
            _logger.LogError("[Diagnostics] Failed to connect to IBKR");
            _lifetime.StopApplication();
            return;
        }

        // Start reader thread
        var reader = new EReader(_client, signal);
        reader.Start();
        _ = Task.Run(() =>
        {
            while (_client.IsConnected())
            {
                signal.waitForSignal();
                reader.processMsgs();
            }
        }, cancellationToken);

        await Task.Delay(2000, cancellationToken); // Wait for connection to stabilize

        // Run diagnostics for each symbol
        foreach (var symbol in symbols)
        {
            await RunDiagnosticsForSymbol(symbol, cancellationToken);
        }

        // Print results
        PrintResultsTable();

        _logger.LogInformation("[Diagnostics] Diagnostics complete. Stopping application.");
        _lifetime.StopApplication();
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        if (_client?.IsConnected() == true)
        {
            _client.eDisconnect();
        }
        return Task.CompletedTask;
    }

    public void Dispose()
    {
        if (_client?.IsConnected() == true)
        {
            _client.eDisconnect();
        }
    }

    private async Task RunDiagnosticsForSymbol(string symbol, CancellationToken cancellationToken)
    {
        _logger.LogInformation("[Diagnostics] Testing {Symbol}...", symbol);

        // Get contract classification
        var classifications = await _classificationService.GetClassificationsAsync(new[] { symbol }, cancellationToken);
        if (!classifications.TryGetValue(symbol, out var classification) || classification == null)
        {
            _logger.LogWarning("[Diagnostics] {Symbol}: No contract classification found", symbol);
            _results.Add(new DiagnosticResult
            {
                Symbol = symbol,
                PrimaryExchange = "UNKNOWN",
                UsedExchange = "N/A",
                ErrorMessage = "NoClassification"
            });
            return;
        }

        var primaryExchange = classification.PrimaryExchange ?? "UNKNOWN";

        // Test 1: Try primary exchange
        var primaryResult = await TestExchange(symbol, primaryExchange, classification, cancellationToken);

        // Test 2: If primary failed, try SMART
        DiagnosticResult? smartResult = null;
        if (!primaryResult.GotL1 && !primaryResult.GotTape && primaryExchange != "SMART")
        {
            _logger.LogInformation("[Diagnostics] {Symbol}: Primary exchange {Primary} failed, trying SMART", 
                symbol, primaryExchange);
            smartResult = await TestExchange(symbol, "SMART", classification, cancellationToken);
        }

        // Store best result
        var finalResult = smartResult ?? primaryResult;
        finalResult.PrimaryExchange = primaryExchange;
        _results.Add(finalResult);
    }

    private async Task<DiagnosticResult> TestExchange(
        string symbol, 
        string exchange, 
        ContractClassification classification,
        CancellationToken cancellationToken)
    {
        var result = new DiagnosticResult
        {
            Symbol = symbol,
            PrimaryExchange = classification.PrimaryExchange ?? "UNKNOWN",
            UsedExchange = exchange
        };

        var contract = new Contract
        {
            Symbol = symbol,
            SecType = classification.SecType,
            Exchange = exchange,
            Currency = classification.Currency ?? "USD",
            ConId = classification.ConId
        };

        if (!string.IsNullOrEmpty(classification.PrimaryExchange))
        {
            contract.PrimaryExch = classification.PrimaryExchange;
        }

        var mktDataReqId = Interlocked.Increment(ref _nextRequestId);
        var tapeReqId = Interlocked.Increment(ref _nextRequestId);

        var session = new DiagnosticSession
        {
            Symbol = symbol,
            Exchange = exchange,
            MktDataRequestId = mktDataReqId,
            TapeRequestId = tapeReqId,
            StartTimeMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
        };

        _activeSessions[mktDataReqId] = session;
        _activeSessions[tapeReqId] = session;

        try
        {
            // Subscribe to market data (L1)
            _client!.reqMktData(mktDataReqId, contract, string.Empty, false, false, null);
            
            // Subscribe to tick-by-tick (tape)
            _client.reqTickByTickData(tapeReqId, contract, "AllLast", 0, false);

            // Wait for test duration
            await Task.Delay(TestDurationMs, cancellationToken);

            // Unsubscribe
            _client.cancelMktData(mktDataReqId);
            _client.cancelTickByTickData(tapeReqId);

            result.GotL1 = session.GotL1;
            result.GotTape = session.GotTape;
            result.GotDepth = session.GotDepth;
            result.FirstRecvMs = session.FirstRecvMs;
            result.LastRecvMs = session.LastRecvMs;
            result.ErrorCodes = session.ErrorCodes.ToList();
            result.L1Count = session.L1Count;
            result.TapeCount = session.TapeCount;
            result.DepthCount = session.DepthCount;

            _activeSessions.TryRemove(mktDataReqId, out _);
            _activeSessions.TryRemove(tapeReqId, out _);

            var nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            result.LastRecvAgeMs = result.LastRecvMs.HasValue 
                ? nowMs - result.LastRecvMs.Value 
                : null;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[Diagnostics] Error testing {Symbol} on {Exchange}", symbol, exchange);
            result.ErrorMessage = ex.Message;
        }

        return result;
    }

    private void PrintResultsTable()
    {
        _logger.LogInformation("[Diagnostics] ========================================");
        _logger.LogInformation("[Diagnostics] SUBSCRIPTION DIAGNOSTICS RESULTS");
        _logger.LogInformation("[Diagnostics] ========================================");
        _logger.LogInformation("[Diagnostics] {Header}",
            "Symbol".PadRight(8) + " | " +
            "Primary".PadRight(8) + " | " +
            "Used".PadRight(8) + " | " +
            "L1?".PadRight(5) + " | " +
            "Tape?".PadRight(6) + " | " +
            "Depth?".PadRight(7) + " | " +
            "L1Cnt".PadRight(6) + " | " +
            "TapeCnt".PadRight(8) + " | " +
            "FirstMs".PadRight(8) + " | " +
            "AgeMs".PadRight(8) + " | " +
            "Errors");

        _logger.LogInformation("[Diagnostics] {Separator}", new string('-', 120));

        foreach (var result in _results)
        {
            var l1Status = result.GotL1 ? "YES" : "NO";
            var tapeStatus = result.GotTape ? "YES" : "NO";
            var depthStatus = result.GotDepth ? "YES" : "NO";
            var firstMs = result.FirstRecvMs?.ToString() ?? "N/A";
            var ageMs = result.LastRecvAgeMs?.ToString() ?? "N/A";
            var errors = result.ErrorCodes.Count > 0 
                ? string.Join(",", result.ErrorCodes) 
                : (result.ErrorMessage ?? "None");

            _logger.LogInformation("[Diagnostics] {Row}",
                result.Symbol.PadRight(8) + " | " +
                result.PrimaryExchange.PadRight(8) + " | " +
                result.UsedExchange.PadRight(8) + " | " +
                l1Status.PadRight(5) + " | " +
                tapeStatus.PadRight(6) + " | " +
                depthStatus.PadRight(7) + " | " +
                result.L1Count.ToString().PadRight(6) + " | " +
                result.TapeCount.ToString().PadRight(8) + " | " +
                firstMs.PadRight(8) + " | " +
                ageMs.PadRight(8) + " | " +
                errors);
        }

        _logger.LogInformation("[Diagnostics] ========================================");

        // Print analysis
        _logger.LogInformation("[Diagnostics] ANALYSIS:");
        foreach (var result in _results)
        {
            var analysis = AnalyzeResult(result);
            _logger.LogInformation("[Diagnostics]   {Symbol}: {Analysis}", result.Symbol, analysis);
        }
    }

    private string AnalyzeResult(DiagnosticResult result)
    {
        if (!string.IsNullOrEmpty(result.ErrorMessage))
        {
            return result.ErrorMessage;
        }

        if (result.ErrorCodes.Contains(10092))
        {
            return "Deep market data not enabled (error 10092)";
        }

        if (result.ErrorCodes.Contains(10167))
        {
            return "Tick-by-tick subscription limit reached (error 10167)";
        }

        if (result.ErrorCodes.Contains(200))
        {
            return "No security definition found (error 200)";
        }

        if (!result.GotL1 && !result.GotTape)
        {
            if (result.UsedExchange == "SMART")
            {
                return "No data even on SMART - symbol not trading or entitlement missing";
            }
            return $"No data on {result.UsedExchange} - try SMART or check entitlement";
        }

        if (result.GotL1 && !result.GotTape)
        {
            return "L1 working but no tick-by-tick - may need different subscription or entitlement";
        }

        if (!result.GotL1)
        {
            // GotTape must be true here (since !GotL1 && !GotTape was handled above)
            return "Tick-by-tick working but no L1 - unusual, check subscription";
        }

        // Both GotL1 and GotTape are true
        return $"Working on {result.UsedExchange} - L1: {result.L1Count}, Tape: {result.TapeCount}";
    }

    // Lightweight IBKR wrapper to track diagnostic session events
    private sealed class DiagnosticEWrapper : DefaultEWrapper
    {
        private readonly ILogger _logger;
        private readonly ConcurrentDictionary<int, DiagnosticSession> _sessions;

        public DiagnosticEWrapper(ILogger logger, ConcurrentDictionary<int, DiagnosticSession> sessions)
        {
            _logger = logger;
            _sessions = sessions;
        }

        public override void tickPrice(int tickerId, int field, double price, TickAttrib attrib)
        {
            // Field 4 = LAST price (L1)
            if (field == 4 && _sessions.TryGetValue(tickerId, out var session))
            {
                var nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                session.GotL1 = true;
                session.L1Count++;
                session.FirstRecvMs ??= nowMs;
                session.LastRecvMs = nowMs;
            }
        }

        public override void tickSize(int tickerId, int field, int size)
        {
            // Field 5 = LAST_SIZE (L1)
            if (field == 5 && _sessions.TryGetValue(tickerId, out var session))
            {
                var nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                session.GotL1 = true;
                session.L1Count++;
                session.FirstRecvMs ??= nowMs;
                session.LastRecvMs = nowMs;
            }
        }

        public override void tickByTickAllLast(int reqId, int tickType, long time, double price, int size, TickAttribLast tickAttribLast, string exchange, string specialConditions)
        {
            if (_sessions.TryGetValue(reqId, out var session))
            {
                var nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                session.GotTape = true;
                session.TapeCount++;
                session.FirstRecvMs ??= nowMs;
                session.LastRecvMs = nowMs;
            }
        }

        public override void updateMktDepth(int tickerId, int position, int operation, int side, double price, int size)
        {
            if (_sessions.TryGetValue(tickerId, out var session))
            {
                var nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                session.GotDepth = true;
                session.DepthCount++;
                session.FirstRecvMs ??= nowMs;
                session.LastRecvMs = nowMs;
            }
        }

        public override void error(int id, int errorCode, string errorMsg)
        {
            if (_sessions.TryGetValue(id, out var session))
            {
                if (!session.ErrorCodes.Contains(errorCode))
                {
                    session.ErrorCodes.Add(errorCode);
                }
                _logger.LogWarning("[Diagnostics] {Symbol} on {Exchange}: Error {Code} - {Msg}", 
                    session.Symbol, session.Exchange, errorCode, errorMsg);
            }
        }
    }

    private sealed class DiagnosticSession
    {
        public string Symbol { get; init; } = string.Empty;
        public string Exchange { get; init; } = string.Empty;
        public int MktDataRequestId { get; init; }
        public int TapeRequestId { get; init; }
        public long StartTimeMs { get; init; }
        public bool GotL1 { get; set; }
        public bool GotTape { get; set; }
        public bool GotDepth { get; set; }
        public long? FirstRecvMs { get; set; }
        public long? LastRecvMs { get; set; }
        public List<int> ErrorCodes { get; } = new();
        public int L1Count { get; set; }
        public int TapeCount { get; set; }
        public int DepthCount { get; set; }
    }

    private sealed class DiagnosticResult
    {
        public string Symbol { get; init; } = string.Empty;
        public string PrimaryExchange { get; set; } = string.Empty;
        public string UsedExchange { get; init; } = string.Empty;
        public bool GotL1 { get; set; }
        public bool GotTape { get; set; }
        public bool GotDepth { get; set; }
        public long? FirstRecvMs { get; set; }
        public long? LastRecvMs { get; set; }
        public long? LastRecvAgeMs { get; set; }
        public List<int> ErrorCodes { get; set; } = new();
        public int L1Count { get; set; }
        public int TapeCount { get; set; }
        public int DepthCount { get; set; }
        public string? ErrorMessage { get; set; }
    }
}
