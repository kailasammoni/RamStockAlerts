using System;
using IBApi;
using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using RamStockAlerts.Services;
using RamStockAlerts.Services.Universe;

namespace RamStockAlerts.Universe;

public sealed class IbkrScannerUniverseSource : IUniverseSource
{
    private readonly IConfiguration _configuration;
    private readonly ILogger<IbkrScannerUniverseSource> _logger;
    private readonly ContractClassificationService _classificationService;
    private readonly IRequestIdSource _requestIdSource;
    private readonly object _cacheLock = new();
    private IReadOnlyList<string> _lastUniverse = Array.Empty<string>();
    private DateTime _lastScanTimeUtc = DateTime.MinValue;
    private bool _lastUniverseIsStale = false;
    private static readonly TimeSpan MinScanInterval = TimeSpan.FromMinutes(5);
    private readonly int _startHourEt;
    private readonly int _startMinuteEt;
    private readonly int _endHourEt;
    private readonly int _endMinuteEt;
    private readonly TimeZoneInfo _eastern;
    private readonly string _cacheFilePath;

    public IbkrScannerUniverseSource(
        IConfiguration configuration,
        ILogger<IbkrScannerUniverseSource> logger,
        ContractClassificationService classificationService,
        IRequestIdSource requestIdSource)
    {
        _startHourEt = Math.Clamp(configuration.GetValue("Universe:IbkrScanner:StartHour", 7), 0, 23);
        _startMinuteEt = Math.Clamp(configuration.GetValue("Universe:IbkrScanner:StartMinute", 0), 0, 59);
        _endHourEt = Math.Clamp(configuration.GetValue("Universe:IbkrScanner:EndHour", 16), 0, 23);
        _endMinuteEt = Math.Clamp(configuration.GetValue("Universe:IbkrScanner:EndMinute", 0), 0, 59);
        _eastern = TryGetEasternTimeZone();
        
        _cacheFilePath = configuration["Universe:IbkrScanner:CacheFilePath"] ?? "logs/universe-cache.jsonl";
        
        // Ensure cache directory exists
        try
        {
            var cacheDir = Path.GetDirectoryName(_cacheFilePath);
            if (!string.IsNullOrEmpty(cacheDir) && !Directory.Exists(cacheDir))
            {
                Directory.CreateDirectory(cacheDir);
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "[IBKR Scanner] Failed to create cache directory for {CacheFile}", _cacheFilePath);
        }

        _configuration = configuration;
        _logger = logger;
        _classificationService = classificationService ?? throw new ArgumentNullException(nameof(classificationService));
        _requestIdSource = requestIdSource ?? throw new ArgumentNullException(nameof(requestIdSource));
    }

    public async Task<IReadOnlyList<string>> GetUniverseAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        // Rate limit: Don't scan more than once per MinScanInterval
        lock (_cacheLock)
        {
            var timeSinceLastScan = DateTime.UtcNow - _lastScanTimeUtc;
            if (timeSinceLastScan < MinScanInterval && _lastUniverse.Count > 0)
            {
                _logger.LogDebug(
                    "[IBKR Scanner] Rate limit: Last scan was {Seconds:F1}s ago (min {MinSeconds}s). Returning cached count={Count}.",
                    timeSinceLastScan.TotalSeconds,
                    MinScanInterval.TotalSeconds,
                    _lastUniverse.Count);
                return _lastUniverse;
            }
        }

        if (!IsWithinOperatingWindow())
        {
            var cached = GetCachedUniverse();
            _logger.LogDebug(
                "[IBKR Scanner] Skipping universe refresh outside window {StartHour:D2}:{StartMinute:D2}-{EndHour:D2}:{EndMinute:D2} ET. Returning cached count={Count}.",
                _startHourEt,
                _startMinuteEt,
                _endHourEt,
                _endMinuteEt,
                cached.Count);
            return cached;
        }

        var host = _configuration["IBKR:Host"] ?? "127.0.0.1";
        var port = _configuration.GetValue<int?>("IBKR:Port") ?? 7496;
        var baseClientId = _configuration.GetValue<int?>("IBKR:ClientId") ?? 1;
        var clientId = _configuration.GetValue<int?>("Universe:IbkrScanner:ClientId") ?? baseClientId + 9; // Offset further to avoid collisions

        var instrument = _configuration["Universe:IbkrScanner:Instrument"] ?? "STK";
        var locationCode = _configuration["Universe:IbkrScanner:LocationCode"] ?? "STK.US.MAJOR";
        
        // Robust reading of ScanCodes array
        var scanCodes = _configuration.GetSection("Universe:IbkrScanner:ScanCodes")
            .GetChildren()
            .Select(x => x.Value)
            .Where(x => !string.IsNullOrEmpty(x))
            .ToList();

        if (scanCodes.Count == 0)
        {
            var legacyCode = _configuration["Universe:IbkrScanner:ScanCode"];
            if (!string.IsNullOrEmpty(legacyCode))
            {
                scanCodes.Add(legacyCode);
            }
            else
            {
                scanCodes.Add("HOT_BY_VOLUME"); // Sensible default
            }
        }

        _logger.LogInformation("[IBKR Scanner] Final scan codes to run: {Codes}", string.Join(", ", scanCodes));
        var rows = Math.Clamp(_configuration.GetValue("Universe:IbkrScanner:Rows", 200), 1, 500);
        var abovePrice = _configuration.GetValue<double?>("Universe:IbkrScanner:AbovePrice") ?? 5d;
        var belowPrice = _configuration.GetValue<double?>("Universe:IbkrScanner:BelowPrice") ?? 20d;
        var aboveVolume = _configuration.GetValue<int?>("Universe:IbkrScanner:AboveVolume") ?? 500_000;
        var floatSharesBelow = _configuration.GetValue<double?>("Universe:IbkrScanner:FloatSharesBelow") ?? 150_000_000d;
        var marketCapAbove = _configuration.GetValue<double?>("Universe:IbkrScanner:MarketCapAbove");
        var marketCapBelow = _configuration.GetValue<double?>("Universe:IbkrScanner:MarketCapBelow");

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
                var cumulativeUniverse = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var successCount = 0;

                foreach (var code in scanCodes)
                {
                    try
                    {
                        _logger.LogInformation("[IBKR Scanner] Running scan: {ScanCode}", code);
                        var scanResults = await ExecuteScannerAttemptAsync(
                            host,
                            port,
                            clientId,
                            instrument,
                            locationCode,
                            code,
                            rows,
                            abovePrice,
                            belowPrice,
                            aboveVolume,
                            floatSharesBelow,
                            marketCapAbove,
                            marketCapBelow,
                            cancellationToken);
                        
                        foreach (var s in scanResults) cumulativeUniverse.Add(s);
                        _logger.LogInformation("[IBKR Scanner] Scan {ScanCode} returned {Count} symbols.", code, scanResults.Count);
                        successCount++;
                    }
                    catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
                    {
                        _logger.LogError(ex, "[IBKR Scanner] Individual scan code {ScanCode} failed. Skipping and continuing with next.", code);
                        // Continue to next scan code so one failure doesn't block the whole universe
                    }
                }

                if (successCount == 0 && scanCodes.Count > 0)
                {
                    throw new InvalidOperationException($"All {scanCodes.Count} configured scan codes failed. IBKR connection or farm status likely the cause.");
                }

                var universe = cumulativeUniverse.ToList();

                if (universe.Count > 0)
                {
                    lock (_cacheLock)
                    {
                        _lastUniverse = universe;
                        _lastScanTimeUtc = DateTime.UtcNow;
                        _lastUniverseIsStale = false;
                    }
                    
                    // Save to persistent cache
                    _ = SaveUniverseCacheAsync(universe);
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

        // Scanner failed on all attempts - try cache fallback
        _logger.LogWarning("[IBKR Scanner] All {MaxRetries} attempts failed. Loading from persistent cache for fallback recovery.", maxRetries);
        var cachedUniverse = await LoadUniverseCacheAsync();
        
        if (cachedUniverse.Count > 0)
        {
            lock (_cacheLock)
            {
                _lastUniverse = cachedUniverse;
                _lastUniverseIsStale = true;
            }
            
            _logger.LogWarning(
                "[IBKR Scanner] Cache fallback SUCCESS: recovered {Count} symbols from persistent cache (stale). IBKR resilience: universe source via fallback. Real-time scanning will retry on next refresh.",
                cachedUniverse.Count);
            LogUniverse(cachedUniverse);
            return cachedUniverse;
        }
        
        _logger.LogError("[IBKR Scanner] Cache fallback FAILED: No cache available and all {MaxRetries} scan attempts failed. IBKR resilience: universe source exhausted.", maxRetries);
        throw lastException ?? new InvalidOperationException("IBKR scanner request failed and no cache available.");
    }

    internal async Task<IReadOnlyList<string>> ExecuteScannerAttemptAsync(
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
        double? floatSharesBelow,
        double? marketCapAbove,
        double? marketCapBelow,
        CancellationToken cancellationToken)
    {
        var requestId = _requestIdSource.NextId();
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
                AboveVolume = aboveVolume
            };

            _logger.LogInformation(
                "[IBKR Scanner] Requesting scan {ScanCode} {Instrument} {Location} rows={Rows} price={AbovePrice}-{BelowPrice} volume>={AboveVolume} float<{FloatBelow}",
                scanCode,
                instrument,
                locationCode,
                rows,
                abovePrice,
                belowPrice,
                aboveVolume,
                floatSharesBelow);

            var filterOptions = BuildFilterOptions(scanCode, floatSharesBelow);

            client.reqScannerSubscription(
                requestId,
                subscription,
                new List<TagValue>(),
                filterOptions);

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(35)); // Increased from 10s to 30s for open load

            var completed = await Task.WhenAny(
                completion.Task,
                Task.Delay(Timeout.Infinite, timeoutCts.Token));

            if (completed != completion.Task)
            {
                throw new TimeoutException("IBKR scanner request timed out.");
            }

            var universe = await completion.Task;

            // Preload classifications so downstream filters have stock type + primary exchange.
            _logger.LogInformation("[IBKR Scanner] Starting classification prefetch for {Count} symbols", universe.Count);
            try
            {
                await _classificationService.GetClassificationsAsync(universe, cancellationToken);
                _logger.LogInformation("[IBKR Scanner] Classification prefetch completed for {Count} symbols", universe.Count);
            }
            catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
            {
                _logger.LogWarning(ex, "[IBKR Scanner] Prefetch classifications failed.");
            }

            return universe;
        }
        catch (System.IO.EndOfStreamException ex)
        {
            _logger.LogError(ex, "[IBKR Scanner] Connection to TWS dropped (End of Stream). This often happens if TWS market data farms are disconnected or the client ID is already in use.");
            throw;
        }
        catch (Exception ex) when (ex.Message.Contains("read beyond the end of the stream"))
        {
             _logger.LogError(ex, "[IBKR Scanner] Connection to TWS dropped. Check TWS -> Troubleshooting -> Diagnostics -> Data Connections.");
             throw;
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

    /// <summary>
    /// Saves universe to persistent cache file as JSONL.
    /// Each line is JSON with timestamp and symbols array.
    /// </summary>
    private async Task SaveUniverseCacheAsync(IReadOnlyList<string> universe)
    {
        try
        {
            var cacheEntry = new
            {
                timestamp = DateTime.UtcNow.ToString("O"),
                count = universe.Count,
                symbols = universe
            };

            var json = JsonSerializer.Serialize(cacheEntry);
            
            // Append to JSONL file
            await File.AppendAllTextAsync(
                _cacheFilePath,
                json + Environment.NewLine);

            _logger.LogDebug("[IBKR Scanner] Saved universe to cache: {File} ({Count} symbols)", _cacheFilePath, universe.Count);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[IBKR Scanner] Failed to save universe cache to {File}", _cacheFilePath);
        }
    }

    /// <summary>
    /// Loads latest universe from persistent cache file.
    /// Returns empty if no cache exists or file is corrupted.
    /// </summary>
    private async Task<IReadOnlyList<string>> LoadUniverseCacheAsync()
    {
        try
        {
            if (!File.Exists(_cacheFilePath))
            {
                _logger.LogDebug("[IBKR Scanner] Cache file does not exist: {File}", _cacheFilePath);
                return Array.Empty<string>();
            }

            var lines = await File.ReadAllLinesAsync(_cacheFilePath);
            if (lines.Length == 0)
            {
                _logger.LogDebug("[IBKR Scanner] Cache file is empty: {File}", _cacheFilePath);
                return Array.Empty<string>();
            }

            // Load latest (last) entry from JSONL
            var lastLine = lines[^1];
            var doc = JsonDocument.Parse(lastLine);
            var root = doc.RootElement;

            if (root.TryGetProperty("symbols", out var symbolsElement))
            {
                var symbols = new List<string>();
                foreach (var symbol in symbolsElement.EnumerateArray())
                {
                    if (symbol.GetString() is string s && !string.IsNullOrEmpty(s))
                    {
                        symbols.Add(s);
                    }
                }

                var timestamp = root.TryGetProperty("timestamp", out var ts) ? ts.GetString() : "unknown";
                _logger.LogInformation(
                    "[IBKR Scanner] Loaded cache from {File}: {Count} symbols (timestamp={Timestamp})",
                    _cacheFilePath,
                    symbols.Count,
                    timestamp);

                return symbols.AsReadOnly();
            }
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "[IBKR Scanner] Cache file corrupted or invalid JSON: {File}", _cacheFilePath);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[IBKR Scanner] Failed to load universe cache from {File}", _cacheFilePath);
        }

        return Array.Empty<string>();
    }

    /// <summary>
    /// Indicates if last universe from cache is stale (from scanner failure).
    /// </summary>
    public bool IsUniverseStale()
    {
        lock (_cacheLock)
        {
            return _lastUniverseIsStale;
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

    private sealed class ScannerWrapper : DefaultEWrapper
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

        public override void scannerData(int reqId, int rank, ContractDetails contractDetails, string distance, string benchmark, string projection, string legsStr)
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

        public override void scannerDataEnd(int reqId)
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

                // Don't cache classifications from scanner data - they're incomplete (null StockType, PrimaryExch, etc.)
                // Let GetClassificationsAsync() → FetchFromIbkrAsync() fetch proper contract details via reqContractDetails()
            }

            var top = result.Take(10).ToArray();
            var topDisplay = top.Length == 0 ? "n/a" : string.Join(", ", top);
            _logger.LogInformation("[IBKR Scanner] Scanner subscription completed with {Count} symbols (top10={Top})", result.Count, topDisplay);

            _completion.TrySetResult(result);
        }

        public override void connectionClosed()
        {
            if (!_completion.Task.IsCompleted)
            {
                List<string> result;
                lock (_symbols)
                {
                    if (_candidates.Count == 0)
                    {
                        _logger.LogWarning("[IBKR Scanner] Connection closed before scanner results were received.");
                        _completion.TrySetException(new InvalidOperationException("IBKR scanner connection closed."));
                        return;
                    }

                    _logger.LogDebug("[IBKR Scanner] Connection closed.");

                    result = _candidates
                        .OrderBy(candidate => candidate.Rank)
                        .Select(candidate => candidate.Symbol)
                        .ToList();

                    CacheClassifications();
                }

                var top = result.Take(10).ToArray();
                var topDisplay = top.Length == 0 ? "n/a" : string.Join(", ", top);
                _logger.LogInformation("[IBKR Scanner] Connection closed, scanner returned {Count} symbols (top10={Top})", result.Count, topDisplay);

                _completion.TrySetResult(result);
            }
        }

        public override void error(int id, long errorTime, int errorCode, string errorMsg, string advancedOrderRejectJson)
        {
            if (errorCode == 2104 || errorCode == 2106 || errorCode == 2158)
            {
                _logger.LogDebug("[IBKR Scanner] Info: id={Id} code={Code} msg={Msg}", id, errorCode, errorMsg);
                return;
            }

            if (errorCode == 165)
            {
                _logger.LogWarning("[IBKR Scanner] Warning code={Code} msg={Msg} (ignoring)", errorCode, errorMsg);
                return;
            }

            _logger.LogWarning("[IBKR Scanner] Error: id={Id} code={Code} msg={Msg}", id, errorCode, errorMsg);

            if (id == _requestId)
            {
                _completion.TrySetException(new InvalidOperationException($"IBKR scanner error {errorCode}: {errorMsg}"));
            }
        }

        public override void error(string str) => _logger.LogError("[IBKR Scanner] {Message}", str);
        public override void error(Exception e) => _logger.LogError(e, "[IBKR Scanner] Exception");

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
                details.Contract?.SecType,
                details.Contract?.Exchange,
                details.Contract?.PrimaryExch,
                details.Contract?.Currency,
                details.Contract?.LocalSymbol,
                details.Contract?.TradingClass,
                details.Contract?.LastTradeDateOrContractMonth,
                details.Contract?.Multiplier,
                ResolveStockType(details),
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

    private static List<TagValue> BuildFilterOptions(
        string scanCode,
        double? floatSharesBelow)
    {
        var options = new List<TagValue>();

        // Apply float filter to all scans if value is provided
        if (floatSharesBelow.HasValue && floatSharesBelow.Value > 0)
        {
            options.Add(new TagValue("floatSharesBelow", floatSharesBelow.Value.ToString(CultureInfo.InvariantCulture)));
        }

        return options;
    }
}
