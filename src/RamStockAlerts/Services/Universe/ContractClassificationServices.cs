using System.Collections.Concurrent;
using System.Text.Json;
using System.Linq;
using IBApi;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using RamStockAlerts.Services;

namespace RamStockAlerts.Services.Universe;

public sealed record ContractClassification(
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
    string StockType,
    DateTimeOffset UpdatedAt)
{
    public ContractClassification(
        string symbol,
        int conId,
        string? secType,
        string? primaryExchange,
        string? currency,
        string stockType,
        DateTimeOffset updatedAt)
        : this(symbol, conId, secType, null, primaryExchange, currency, null, null, null, null, stockType, updatedAt)
    {
    }
}

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
    private readonly IConfiguration _configuration;
    private readonly ILogger<ContractClassificationService> _logger;
    private readonly ContractClassificationCache _cache;
    private readonly IRequestIdSource _requestIdSource;
    private readonly TimeSpan _minInterval;
    private readonly SemaphoreSlim _throttle = new(1, 1);
    private readonly SemaphoreSlim _connectionLock = new(1, 1);
    private DateTimeOffset _lastRequestAtUtc = DateTimeOffset.MinValue;

    public ContractClassificationService(
        IConfiguration configuration,
        ILogger<ContractClassificationService> logger,
        ContractClassificationCache cache,
        IRequestIdSource requestIdSource)
    {
        _configuration = configuration;
        _logger = logger;
        _cache = cache;
        _requestIdSource = requestIdSource;
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
        var host = _configuration["IBKR:Host"] ?? "127.0.0.1";
        var port = _configuration.GetValue<int?>("IBKR:Port") ?? 7496;
        var baseClientId = _configuration.GetValue<int?>("IBKR:ClientId") ?? 1;
        var clientId = _configuration.GetValue<int?>("Universe:IbkrContract:ClientId") ?? baseClientId + 2;

        var readerSignal = new EReaderMonitorSignal();
        var wrapper = new ContractDetailsWrapper(_logger);
        var client = new EClientSocket(wrapper, readerSignal);

        try
        {
            _logger.LogInformation(
                "[IBKR ContractDetails] Connecting to {Host}:{Port} clientId={ClientId}",
                host,
                port,
                clientId);

            client.eConnect(host, port, clientId);

            if (!client.IsConnected())
            {
                _logger.LogWarning("[IBKR ContractDetails] Failed to connect to {Host}:{Port}", host, port);
                return new List<ContractClassification>();
            }

            client.startApi();

            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            var pumpTask = Task.Run(() => ProcessMessages(client, readerSignal, wrapper, linkedCts.Token), linkedCts.Token);

            var tasks = new List<(string Symbol, Task<ContractDetails?> Task)>();
            
            foreach (var symbol in symbols)
            {
                // Simple pacing: fire-and-forget requests with micro-delays
                await Task.Delay(20, cancellationToken); 
                
                var requestId = _requestIdSource.NextId();
                var task = wrapper.Register(requestId, symbol);

                var contract = new Contract
                {
                    Symbol = symbol,
                    SecType = "STK",
                    Exchange = "SMART",
                    Currency = "USD"
                };

                client.reqContractDetails(requestId, contract);
                tasks.Add((symbol, task));
            }

            var results = new List<ContractClassification>();
            var timeout = TimeSpan.FromSeconds(30);

            _logger.LogInformation("[IBKR ContractDetails] Waiting for {Count} requests to complete (timeout: {Timeout}s)...", tasks.Count, timeout.TotalSeconds);

            foreach (var item in tasks)
            {
                try
                {
                    using var timeoutCts = new CancellationTokenSource(timeout);
                    using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);
                    
                    var details = await item.Task.WaitAsync(cts.Token);
                    if (details != null)
                    {
                        var classification = ToClassification(details, DateTimeOffset.UtcNow);
                        await _cache.PutAsync(classification, cancellationToken);
                        results.Add(classification);
                        _logger.LogDebug("[IBKR ContractDetails] Success for {Symbol}", item.Symbol);
                    }
                }
                catch (OperationCanceledException)
                {
                    _logger.LogWarning("[IBKR ContractDetails] Timeout or cancellation for {Symbol}", item.Symbol);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning("[IBKR ContractDetails] Error for {Symbol}: {Message}", item.Symbol, ex.Message);
                }
            }

            return results;
        }
        finally
        {
            _connectionLock.Release();
            if (client.IsConnected())
            {
                client.eDisconnect();
            }
            readerSignal.issueSignal();
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
            details.Contract?.Exchange,
            details.Contract?.PrimaryExch,
            details.Contract?.Currency,
            details.Contract?.LocalSymbol,
            details.Contract?.TradingClass,
            details.Contract?.LastTradeDateOrContractMonth,
            details.Contract?.Multiplier,
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

        while (!stoppingToken.IsCancellationRequested && socket.IsConnected())
        {
            signal.waitForSignal();
            try
            {
                reader.processMsgs();
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                break;
            }
        }
    }

    private sealed class ContractDetailsWrapper : DefaultEWrapper
    {
        private readonly ILogger _logger;
        private readonly ConcurrentDictionary<int, TaskCompletionSource<ContractDetails?>> _pending = new();

        public ContractDetailsWrapper(ILogger logger)
        {
            _logger = logger;
        }

        public Task<ContractDetails?> Register(int requestId, string symbol)
        {
            var tcs = new TaskCompletionSource<ContractDetails?>(TaskCreationOptions.RunContinuationsAsynchronously);
            _pending[requestId] = tcs;
            return tcs.Task;
        }

        public override void contractDetails(int reqId, ContractDetails contractDetails)
        {
            if (_pending.TryGetValue(reqId, out var tcs) && contractDetails is not null)
            {
                tcs.TrySetResult(contractDetails);
            }
        }

        public override void contractDetailsEnd(int reqId)
        {
            if (_pending.TryRemove(reqId, out var tcs))
            {
                // If it wasn't already set by contractDetails, it's finished but empty
                tcs.TrySetResult(null);
            }
        }

        public override void error(int id, long errorTime, int errorCode, string errorMsg, string advancedOrderRejectJson)
        {
            // Error 200: No security definition found. This is common for invalid tickers (e.g. ETFs being scanned).
            if (errorCode == 200 && _pending.TryRemove(id, out var tcs))
            {
                _logger.LogDebug("[IBKR ContractDetails] Symbol not found (ErrorCode 200) for id={Id}", id);
                tcs.TrySetResult(null);
                return;
            }

            if (errorCode == 2104 || errorCode == 2106 || errorCode == 2158 || errorCode == 2107)
            {
                // These are just farm connection status messages
                return;
            }

            if (id > 0 && _pending.TryRemove(id, out var errorTcs))
            {
                _logger.LogDebug("[IBKR ContractDetails] Error id={Id} code={Code} msg={Msg}", id, errorCode, errorMsg);
                errorTcs.TrySetException(new Exception($"IBKR {errorCode}: {errorMsg}"));
            }
        }

        public override void error(string str) => _logger.LogDebug("[IBKR ContractDetails] {Message}", str);
        public override void error(Exception e) => _logger.LogDebug(e, "[IBKR ContractDetails] Exception");
    }
}
