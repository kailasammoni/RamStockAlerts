using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using RamStockAlerts.Services.Universe;

namespace RamStockAlerts.Services;

public enum MarketDataRequestKind
{
    MktData,
    Depth,
    TickByTick
}

public sealed record MarketDataSubscription(
    string Symbol,
    int? MktDataRequestId,
    int? DepthRequestId,
    int? TickByTickRequestId,
    string? DepthExchange);

public sealed record SubscriptionStats(
    int TotalSubscriptions,
    int DepthEnabled,
    int TickByTickEnabled,
    long DepthSubscribeAttempts,
    long DepthSubscribeUpdateReceived,
    long DepthSubscribeErrors,
    IReadOnlyDictionary<int, int> DepthSubscribeErrorsByCode,
    int? LastDepthErrorCode,
    string? LastDepthErrorMessage);

public sealed record DepthRetryPlan(
    string Symbol,
    int ConId,
    string SecType,
    string? PrimaryExchange,
    string? Currency,
    string PreviousExchange);

public sealed class MarketDataSubscriptionManager
{
    private static readonly TimeSpan DepthCooldown = TimeSpan.FromDays(1);
    private static readonly TimeSpan TickByTickCooldown = TimeSpan.FromMinutes(30);

    private readonly IConfiguration _configuration;
    private readonly ILogger<MarketDataSubscriptionManager> _logger;
    private readonly ContractClassificationService _classificationService;
    private readonly DepthEligibilityCache _depthEligibilityCache;
    private readonly SemaphoreSlim _sync = new(1, 1);
    private readonly ConcurrentDictionary<string, SubscriptionState> _active = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<int, RequestMapping> _requestMap = new();
    private readonly ConcurrentDictionary<string, DateTimeOffset> _depthDisabledUntil = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, DateTimeOffset> _tickByTickDisabledUntil = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _depthDiagnosticsLock = new();
    private long _depthSubscribeAttempts;
    private long _depthSubscribeUpdateReceived;
    private long _depthSubscribeErrors;
    private readonly ConcurrentDictionary<int, int> _depthSubscribeErrorsByCode = new();
    private int? _lastDepthErrorCode;
    private string? _lastDepthErrorMessage;
    private IReadOnlyList<string> _lastTapeEnabledLog = Array.Empty<string>();
    private IReadOnlyList<string> _lastEligibleLog = Array.Empty<string>();
    private IReadOnlyList<string> _lastUniverse = Array.Empty<string>();
    private int _lastMaxLines;
    private int _lastTickByTickMaxSymbols;
    private ShadowTradingHelpers.TapeGateConfig? _lastTapeGateConfig;
    private readonly ConcurrentDictionary<string, byte> _depthIneligibleLogged = new(StringComparer.OrdinalIgnoreCase);
    private readonly Queue<string> _lastDepthIneligibleSymbols = new();
    private const int LastDepthIneligibleSymbolsLimit = 5;
    private readonly ConcurrentDictionary<int, DateTimeOffset> _pendingTickByTickCancels = new();
    private static readonly TimeSpan PendingCancelTtl = TimeSpan.FromMinutes(2);
    private bool _skipTickByTickEnableThisCycle;
    private bool _tickByTickCapLogged;

    public MarketDataSubscriptionManager(
        IConfiguration configuration,
        ILogger<MarketDataSubscriptionManager> logger,
        ContractClassificationService classificationService,
        DepthEligibilityCache depthEligibilityCache)
    {
        _configuration = configuration;
        _logger = logger;
        _classificationService = classificationService;
        _depthEligibilityCache = depthEligibilityCache;
        var maxLines = _configuration.GetValue("MarketData:MaxLines", 95);
        var tickByTickMaxSymbols = _configuration.GetValue("MarketData:TickByTickMaxSymbols", 10);
        var depthRows = _configuration.GetValue("MarketData:DepthRows", 5);
        _logger.LogInformation(
            "[MarketData] Caps maxLines={MaxLines} tickByTickMaxSymbols={TickByTickMaxSymbols} depthRows={DepthRows}",
            maxLines,
            tickByTickMaxSymbols,
            depthRows);
    }

    public SubscriptionStats GetSubscriptionStats()
    {
        var depth = 0;
        var tick = 0;
        foreach (var state in _active.Values)
        {
            if (state.DepthRequestId.HasValue)
            {
                depth++;
            }

            if (state.TickByTickRequestId.HasValue)
            {
                tick++;
            }
        }

        var attempts = Interlocked.Read(ref _depthSubscribeAttempts);
        var updateReceived = Interlocked.Read(ref _depthSubscribeUpdateReceived);
        var errors = Interlocked.Read(ref _depthSubscribeErrors);
        int? lastCode;
        string? lastMessage;
        lock (_depthDiagnosticsLock)
        {
            lastCode = _lastDepthErrorCode;
            lastMessage = _lastDepthErrorMessage;
        }

        var errorsByCode = _depthSubscribeErrorsByCode.ToDictionary(pair => pair.Key, pair => pair.Value);
        return new SubscriptionStats(_active.Count, depth, tick, attempts, updateReceived, errors, errorsByCode, lastCode, lastMessage);
    }

    public IReadOnlyList<string> GetTickByTickSymbols()
    {
        return _active.Values
            .Where(state => state.TickByTickRequestId.HasValue)
            .Select(state => state.Symbol)
            .OrderBy(symbol => symbol)
            .ToList();
    }

    public IReadOnlyCollection<string> GetTapeEnabledSymbols()
    {
        return GetTickByTickSymbols();
    }

    public IReadOnlyCollection<string> GetDepthEnabledSymbols()
    {
        return _active.Values
            .Where(state => state.DepthRequestId.HasValue)
            .Select(state => state.Symbol)
            .OrderBy(symbol => symbol)
            .ToList();
    }

    public IReadOnlyCollection<string> GetEligibleSymbols()
    {
        var depth = GetDepthEnabledSymbols();
        var tape = new HashSet<string>(GetTapeEnabledSymbols(), StringComparer.OrdinalIgnoreCase);
        return depth.Where(tape.Contains).OrderBy(symbol => symbol).ToList();
    }

    public bool IsTapeEnabled(string symbol)
    {
        if (string.IsNullOrWhiteSpace(symbol))
        {
            return false;
        }

        return _active.TryGetValue(symbol.Trim().ToUpperInvariant(), out var state)
            && state.TickByTickRequestId.HasValue;
    }

    public bool IsDepthEnabled(string symbol)
    {
        if (string.IsNullOrWhiteSpace(symbol))
        {
            return false;
        }

        return _active.TryGetValue(symbol.Trim().ToUpperInvariant(), out var state)
            && state.DepthRequestId.HasValue;
    }

    public bool IsEligibleSymbol(string symbol)
    {
        return IsDepthEnabled(symbol) && IsTapeEnabled(symbol);
    }

    public bool IsFocusSymbol(string symbol)
    {
        if (string.IsNullOrWhiteSpace(symbol))
        {
            return false;
        }

        return IsTapeEnabled(symbol);
    }

    public void RecordActivity(string symbol)
    {
        if (string.IsNullOrWhiteSpace(symbol))
        {
            return;
        }

        if (_active.TryGetValue(symbol.Trim().ToUpperInvariant(), out var state))
        {
            state.LastActivityUtc = DateTimeOffset.UtcNow;
        }
    }

    public async Task ApplyUniverseAsync(
        IReadOnlyList<string> universe,
        Func<string, bool, CancellationToken, Task<MarketDataSubscription?>> subscribeAsync,
        Func<string, CancellationToken, Task<bool>> unsubscribeAsync,
        Func<string, CancellationToken, Task<int?>> enableTickByTickAsync,
        Func<string, CancellationToken, Task<bool>> disableTickByTickAsync,
        Func<string, CancellationToken, Task<bool>> disableDepthAsync,
        CancellationToken cancellationToken)
    {
        var maxActiveSymbols = Math.Max(0, _configuration.GetValue("Universe:MaxActiveSymbols", 0));
        if (maxActiveSymbols > 0 && universe.Count > maxActiveSymbols)
        {
            universe = universe.Take(maxActiveSymbols).ToList();
        }

        var normalizedUniverse = NormalizeUniverse(universe);
        var universeSet = new HashSet<string>(normalizedUniverse, StringComparer.OrdinalIgnoreCase);
        var classifications = await _classificationService.GetClassificationsAsync(normalizedUniverse, cancellationToken);

        var enableDepth = _configuration.GetValue("MarketData:EnableDepth", true);
        var enableTape = _configuration.GetValue("MarketData:EnableTape", true);
        if (!enableDepth && !enableTape)
        {
            _logger.LogWarning("[MarketData] Subscriptions disabled (EnableDepth=false, EnableTape=false).");
            return;
        }

        var maxLines = _configuration.GetValue("MarketData:MaxLines", 95);
        var minHoldMinutes = _configuration.GetValue("MarketData:MinHoldMinutes", 5);
        var minHold = TimeSpan.FromMinutes(Math.Max(0, minHoldMinutes));
        var tickByTickMaxSymbols = Math.Max(0, _configuration.GetValue("MarketData:TickByTickMaxSymbols", 10));
        var minDepthEligibleSymbols = GetMinDepthEligibleSymbols(tickByTickMaxSymbols);

        var now = DateTimeOffset.UtcNow;

        await _sync.WaitAsync(cancellationToken);
        try
        {
            _skipTickByTickEnableThisCycle = false;
            _lastUniverse = normalizedUniverse;
            _lastMaxLines = maxLines;
            _lastTickByTickMaxSymbols = tickByTickMaxSymbols;

            foreach (var symbol in normalizedUniverse)
            {
                if (_active.TryGetValue(symbol, out var state))
                {
                    state.LastSeenUtc = now;
                }
            }

            await DisableDepthIfNeededAsync(enableDepth, disableDepthAsync, now, cancellationToken);

            var totalLines = GetTotalLines();
            if (totalLines > maxLines)
            {
                var linesToFree = totalLines - maxLines;
                linesToFree -= await FreeLinesByDroppingTickByTickAsync(
                    linesToFree,
                    universeSet,
                    disableTickByTickAsync,
                    now,
                    cancellationToken);

                if (linesToFree > 0)
                {
                    await EvictForLinesAsync(
                        linesToFree,
                        universeSet,
                        minHold,
                        allowBeforeHold: true,
                        unsubscribeAsync,
                        cancellationToken,
                        "over cap");
                }
            }

            foreach (var symbol in normalizedUniverse)
            {
                if (_active.ContainsKey(symbol))
                {
                    continue;
                }

                classifications.TryGetValue(symbol, out var classification);
                var requestDepth = enableDepth && !IsDepthDisabled(symbol, now);
                if (requestDepth && !_depthEligibilityCache.CanRequestDepth(classification, symbol, now, out var eligibilityState))
                {
                    _depthEligibilityCache.LogSkipOnce(classification, symbol, eligibilityState);
                    requestDepth = false;
                }

                if (!requestDepth && enableDepth && ShouldPreferDepth(minDepthEligibleSymbols))
                {
                    if (_depthEligibilityCache.CanRequestDepth(classification, symbol, now, out var preferredEligibilityState))
                    {
                        requestDepth = true;
                    }
                }

                var baseLines = (enableTape ? 1 : 0) + (requestDepth ? 1 : 0);
                if (baseLines == 0)
                {
                    continue;
                }

                totalLines = GetTotalLines();
                if (totalLines + baseLines > maxLines)
                {
                    var linesToFree = (totalLines + baseLines) - maxLines;
                    linesToFree -= await FreeLinesByDroppingTickByTickAsync(
                        linesToFree,
                        universeSet,
                        disableTickByTickAsync,
                        now,
                        cancellationToken);

                    if (linesToFree > 0)
                    {
                        var freed = await EvictForLinesAsync(
                            linesToFree,
                            universeSet,
                            minHold,
                            allowBeforeHold: false,
                            unsubscribeAsync,
                            cancellationToken,
                            "make room");

                        if (freed <= 0 || GetTotalLines() + baseLines > maxLines)
                        {
                            _logger.LogInformation(
                                "[MarketData] At cap ({ActiveLines}/{MaxLines}). Holding {Symbol} due to minimum hold time.",
                                GetTotalLines(),
                                maxLines,
                                symbol);
                            continue;
                        }
                    }
                }

                var subscription = await subscribeAsync(symbol, requestDepth, cancellationToken);
                if (subscription is null)
                {
                    _logger.LogWarning("[MarketData] Subscribe failed for {Symbol}.", symbol);
                    continue;
                }

                var state = new SubscriptionState(
                    subscription.Symbol,
                    subscription.MktDataRequestId,
                    subscription.DepthRequestId,
                    subscription.TickByTickRequestId,
                    now,
                    now);
                state.DepthExchange = subscription.DepthExchange;
                _active[subscription.Symbol] = state;
                TrackRequests(state);

                _logger.LogInformation(
                    "[MarketData] Subscribed {Symbol} mktDataId={MktDataId} depthId={DepthId} activeLines={ActiveLines}/{MaxLines}",
                    subscription.Symbol,
                    subscription.MktDataRequestId,
                    subscription.DepthRequestId,
                    GetTotalLines(),
                    maxLines);
            }

            var focusSet = SelectFocusSet(normalizedUniverse, tickByTickMaxSymbols, now);
            await ApplyTickByTickAsync(
                focusSet,
                tickByTickMaxSymbols,
                maxLines,
                enableTickByTickAsync,
                disableTickByTickAsync,
                now,
                cancellationToken);
            LogTapeDepthPairingIfChanged(tickByTickMaxSymbols);
            LogTapeGateConfigIfChanged();
            LogDepthEligibilitySummary(normalizedUniverse, classifications, now);
        }
        finally
        {
            _sync.Release();
        }
    }

    public async Task HandleIbkrErrorAsync(
        int requestId,
        int errorCode,
        string errorMessage,
        Func<string, CancellationToken, Task<bool>> disableDepthAsync,
        Func<string, CancellationToken, Task<bool>> disableTickByTickAsync,
        Func<string, CancellationToken, Task<int?>> enableTickByTickAsync,
        CancellationToken cancellationToken)
    {
        if (errorCode == 300 && _pendingTickByTickCancels.ContainsKey(requestId))
        {
            return;
        }

        if (!_requestMap.TryGetValue(requestId, out var mapping))
        {
            return;
        }

        var symbol = mapping.Symbol;
        if (!_active.TryGetValue(symbol, out var state))
        {
            return;
        }

        var now = DateTimeOffset.UtcNow;
        if (mapping.Kind == MarketDataRequestKind.Depth)
        {
            RecordDepthSubscribeFailure(errorCode, errorMessage);
        }

        if (errorCode == 10092 && mapping.Kind == MarketDataRequestKind.Depth)
        {
            await HandleDepthIneligibleAsync(
                state,
                errorCode,
                errorMessage,
                disableDepthAsync,
                disableTickByTickAsync,
                enableTickByTickAsync,
                now,
                cancellationToken);
            return;
        }

        if (errorCode == 10190 && mapping.Kind == MarketDataRequestKind.TickByTick)
        {
            if (!_tickByTickCapLogged)
            {
                _tickByTickCapLogged = true;
                _logger.LogWarning("TickByTickCapHit: disabling further tick-by-tick enables until next refresh");
            }

            _skipTickByTickEnableThisCycle = true;
            if (IsTickByTickDisabled(symbol, now))
            {
                return;
            }

            _tickByTickDisabledUntil[symbol] = now.Add(TickByTickCooldown);

            if (state.TickByTickRequestId == requestId)
            {
                await disableTickByTickAsync(symbol, cancellationToken);
                MarkPendingCancel(requestId, now);
                UntrackRequest(requestId);
                state.TickByTickRequestId = null;
            }
            else
            {
                UntrackRequest(requestId);
            }

            _logger.LogInformation(
                "Downgrade symbol={Symbol} reason=TickByTickCap action=UseMktDataTapeOnly",
                symbol);
        }
    }

    private async Task DisableDepthIfNeededAsync(
        bool enableDepth,
        Func<string, CancellationToken, Task<bool>> disableDepthAsync,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        foreach (var state in _active.Values)
        {
            if (!state.DepthRequestId.HasValue)
            {
                continue;
            }

            if (!enableDepth || IsDepthDisabled(state.Symbol, now))
            {
                if (await disableDepthAsync(state.Symbol, cancellationToken))
                {
                    UntrackRequest(state.DepthRequestId.Value);
                    state.DepthRequestId = null;
                }
            }
        }
    }

    private async Task HandleDepthIneligibleAsync(
        SubscriptionState state,
        int errorCode,
        string errorMessage,
        Func<string, CancellationToken, Task<bool>> disableDepthAsync,
        Func<string, CancellationToken, Task<bool>> disableTickByTickAsync,
        Func<string, CancellationToken, Task<int?>> enableTickByTickAsync,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        await _sync.WaitAsync(cancellationToken);
        try
        {
            var symbol = state.Symbol;
            if (_depthIneligibleLogged.TryAdd(symbol, 0))
            {
                _logger.LogWarning(
                    "DepthIneligible: symbol={Symbol} code={Code} msg={Msg} -> removing from depth eligibility and rebalancing",
                    symbol,
                    errorCode,
                    errorMessage);
            }

            EnqueueDepthIneligibleSymbol(symbol);

            MarkDepthUnsupported(symbol, $"DepthUnsupported:{errorCode}", now);

            if (state.DepthRequestId.HasValue && await disableDepthAsync(symbol, cancellationToken))
            {
                UntrackRequest(state.DepthRequestId.Value);
                state.DepthRequestId = null;
            }

            if (state.TickByTickRequestId.HasValue && await disableTickByTickAsync(symbol, cancellationToken))
            {
                MarkPendingCancel(state.TickByTickRequestId.Value, now);
                state.TickByTickRequestId = null;
            }

            if (_lastUniverse.Count > 0 && _lastTickByTickMaxSymbols > 0)
            {
                var focusSet = SelectFocusSet(_lastUniverse, _lastTickByTickMaxSymbols, now);
                await ApplyTickByTickAsync(
                    focusSet,
                    _lastTickByTickMaxSymbols,
                    _lastMaxLines,
                    enableTickByTickAsync,
                    disableTickByTickAsync,
                    now,
                    cancellationToken);
            }

            LogTapeDepthPairingIfChanged(_lastTickByTickMaxSymbols);
        }
        finally
        {
            _sync.Release();
        }
    }

    private void EnqueueDepthIneligibleSymbol(string symbol)
    {
        if (string.IsNullOrWhiteSpace(symbol))
        {
            return;
        }

        var normalized = symbol.Trim().ToUpperInvariant();
        if (_lastDepthIneligibleSymbols.Contains(normalized, StringComparer.OrdinalIgnoreCase))
        {
            return;
        }

        _lastDepthIneligibleSymbols.Enqueue(normalized);
        while (_lastDepthIneligibleSymbols.Count > LastDepthIneligibleSymbolsLimit)
        {
            _lastDepthIneligibleSymbols.Dequeue();
        }
    }

    private async Task<int> ApplyTickByTickAsync(
        List<string> focusSet,
        int tickByTickMaxSymbols,
        int maxLines,
        Func<string, CancellationToken, Task<int?>> enableTickByTickAsync,
        Func<string, CancellationToken, Task<bool>> disableTickByTickAsync,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        CleanupPendingCancels(now);
        if (tickByTickMaxSymbols <= 0)
        {
            return 0;
        }

        if (focusSet.Count > tickByTickMaxSymbols)
        {
            focusSet = focusSet.Take(tickByTickMaxSymbols).ToList();
        }

        var focus = new HashSet<string>(focusSet, StringComparer.OrdinalIgnoreCase);

        foreach (var state in _active.Values)
        {
            if (!state.TickByTickRequestId.HasValue)
            {
                continue;
            }

            if (!focus.Contains(state.Symbol) || IsTickByTickDisabled(state.Symbol, now))
            {
                if (await disableTickByTickAsync(state.Symbol, cancellationToken))
                {
                    MarkPendingCancel(state.TickByTickRequestId.Value, now);
                    state.TickByTickRequestId = null;
                }
            }
        }

        var activeTickByTick = _active.Values.Count(state => state.TickByTickRequestId.HasValue);
        if (activeTickByTick >= tickByTickMaxSymbols)
        {
            return activeTickByTick;
        }
        if (_skipTickByTickEnableThisCycle)
        {
            return activeTickByTick;
        }

        foreach (var symbol in focusSet)
        {
            if (activeTickByTick >= tickByTickMaxSymbols)
            {
                break;
            }

            if (!_active.TryGetValue(symbol, out var state))
            {
                continue;
            }

            if (state.TickByTickRequestId.HasValue || IsTickByTickDisabled(symbol, now))
            {
                continue;
            }

            if (GetTotalLines() + 1 > maxLines)
            {
                break;
            }

            var requestId = await enableTickByTickAsync(symbol, cancellationToken);
            if (!requestId.HasValue)
            {
                continue;
            }

            state.TickByTickRequestId = requestId.Value;
            state.LastActivityUtc = now;
            TrackRequest(requestId.Value, symbol, MarketDataRequestKind.TickByTick);
            activeTickByTick++;
        }

        return activeTickByTick;
    }

    private void MarkPendingCancel(int requestId, DateTimeOffset now)
    {
        _pendingTickByTickCancels[requestId] = now;
    }

    private void CleanupPendingCancels(DateTimeOffset now)
    {
        foreach (var entry in _pendingTickByTickCancels)
        {
            if (now - entry.Value <= PendingCancelTtl)
            {
                continue;
            }

            if (_pendingTickByTickCancels.TryRemove(entry.Key, out _))
            {
                UntrackRequest(entry.Key);
            }
        }
    }

    private int GetMinDepthEligibleSymbols(int tickByTickMaxSymbols)
    {
        var defaultMin = Math.Min(3, tickByTickMaxSymbols);
        return Math.Max(0, _configuration.GetValue("MarketData:MinDepthEligibleSymbols", defaultMin));
    }

    private bool ShouldPreferDepth(int minDepthEligibleSymbols)
    {
        if (minDepthEligibleSymbols <= 0)
        {
            return false;
        }

        return GetDepthEnabledSymbols().Count < minDepthEligibleSymbols;
    }

    private void LogDepthEligibilitySummary(
        IReadOnlyList<string> universe,
        IReadOnlyDictionary<string, ContractClassification> classifications,
        DateTimeOffset now)
    {
        var depthEnabled = GetDepthEnabledSymbols().Count;
        var depthUnsupported = 0;
        foreach (var symbol in universe)
        {
            classifications.TryGetValue(symbol, out var classification);
            var state = _depthEligibilityCache.Get(classification, symbol, now);
            if (state.Status == DepthEligibilityStatus.Ineligible)
            {
                depthUnsupported++;
            }
        }

        _logger.LogInformation(
            "DepthEligibilitySummary: universe={UniverseCount} depthEnabled={DepthEnabled} depthUnsupported={DepthUnsupported} last10092Symbols=[{LastSymbols}]",
            universe.Count,
            depthEnabled,
            depthUnsupported,
            string.Join(",", _lastDepthIneligibleSymbols));
    }

    private void LogTapeDepthPairingIfChanged(int tickByTickMaxSymbols)
    {
        var depthEnabled = GetDepthEnabledSymbols().ToList();
        var tapeEnabled = GetTapeEnabledSymbols().ToList();
        var eligible = GetEligibleSymbols().ToList();

        if (_lastTapeEnabledLog.SequenceEqual(tapeEnabled, StringComparer.OrdinalIgnoreCase) &&
            _lastEligibleLog.SequenceEqual(eligible, StringComparer.OrdinalIgnoreCase))
        {
            return;
        }

        _lastTapeEnabledLog = tapeEnabled;
        _lastEligibleLog = eligible;

        _logger.LogInformation(
            "TapeDepthPairing: depthCount={DepthCount} tapeCap={TapeCap} tapeEnabled=[{TapeEnabled}] eligible=[{Eligible}]",
            depthEnabled.Count,
            tickByTickMaxSymbols,
            string.Join(",", tapeEnabled),
            string.Join(",", eligible));
    }

    private void LogTapeGateConfigIfChanged()
    {
        var config = GetTapeGateConfig();
        if (_lastTapeGateConfig.HasValue && _lastTapeGateConfig.Value.Equals(config))
        {
            return;
        }

        _lastTapeGateConfig = config;

        _logger.LogInformation(
            "TapeGateConfig: warmupMinTrades={WarmupMinTrades} warmupWindowMs={WarmupWindowMs} staleWindowMs={StaleWindowMs}",
            config.WarmupMinTrades,
            config.WarmupWindowMs,
            config.StaleWindowMs);
    }

    private ShadowTradingHelpers.TapeGateConfig GetTapeGateConfig()
    {
        var defaults = ShadowTradingHelpers.TapeGateConfig.Default;
        var warmupMinTrades = _configuration.GetValue("MarketData:TapeWarmupMinTrades", defaults.WarmupMinTrades);
        var warmupWindowMs = _configuration.GetValue("MarketData:TapeWarmupWindowMs", defaults.WarmupWindowMs);
        var staleWindowMs = _configuration.GetValue("MarketData:TapeStaleWindowMs", defaults.StaleWindowMs);

        return new ShadowTradingHelpers.TapeGateConfig(
            Math.Max(0, warmupMinTrades),
            Math.Max(0, warmupWindowMs),
            Math.Max(0, staleWindowMs));
    }

    private int GetTotalLines()
    {
        return _active.Values.Sum(GetActiveLineCount);
    }

    private static int GetActiveLineCount(SubscriptionState state)
    {
        var count = 0;
        if (state.MktDataRequestId.HasValue)
        {
            count++;
        }
        if (state.DepthRequestId.HasValue)
        {
            count++;
        }
        if (state.TickByTickRequestId.HasValue)
        {
            count++;
        }

        return count;
    }

    private async Task<int> FreeLinesByDroppingTickByTickAsync(
        int linesNeeded,
        HashSet<string> universeSet,
        Func<string, CancellationToken, Task<bool>> disableTickByTickAsync,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        if (linesNeeded <= 0)
        {
            return 0;
        }

        var candidates = _active.Values
            .Where(state => state.TickByTickRequestId.HasValue)
            .OrderBy(state => universeSet.Contains(state.Symbol) ? 1 : 0)
            .ThenBy(state => GetActivityKey(state));

        var freed = 0;
        foreach (var candidate in candidates)
        {
            if (freed >= linesNeeded)
            {
                break;
            }

            if (await disableTickByTickAsync(candidate.Symbol, cancellationToken) && candidate.TickByTickRequestId.HasValue)
            {
                UntrackRequest(candidate.TickByTickRequestId.Value);
                candidate.TickByTickRequestId = null;
                freed++;
            }
        }

        return freed;
    }

    private async Task<int> EvictForLinesAsync(
        int linesNeeded,
        HashSet<string> universeSet,
        TimeSpan minHold,
        bool allowBeforeHold,
        Func<string, CancellationToken, Task<bool>> unsubscribeAsync,
        CancellationToken cancellationToken,
        string reason)
    {
        if (linesNeeded <= 0)
        {
            return 0;
        }

        var now = DateTimeOffset.UtcNow;
        var candidates = GetEvictionCandidates(universeSet, minHold, allowBeforeHold, now);
        if (candidates.Count == 0)
        {
            return 0;
        }

        var freed = 0;
        foreach (var candidate in candidates)
        {
            if (freed >= linesNeeded)
            {
                break;
            }

            if (!await unsubscribeAsync(candidate.Symbol, cancellationToken))
            {
                _logger.LogWarning("[MarketData] Unsubscribe failed for {Symbol}.", candidate.Symbol);
                continue;
            }

            freed += GetActiveLineCount(candidate);
            RemoveState(candidate.Symbol);

            _logger.LogInformation(
                "[MarketData] Unsubscribed {Symbol} mktDataId={MktDataId} depthId={DepthId} tickByTickId={TickByTickId} reason={Reason} activeLines={ActiveLines}",
                candidate.Symbol,
                candidate.MktDataRequestId,
                candidate.DepthRequestId,
                candidate.TickByTickRequestId,
                reason,
                GetTotalLines());
        }

        return freed;
    }

    private List<string> SelectFocusSet(IReadOnlyList<string> universe, int maxSymbols, DateTimeOffset now)
    {
        if (maxSymbols <= 0)
        {
            return new List<string>();
        }

        var orderIndex = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < universe.Count; i++)
        {
            orderIndex[universe[i]] = i;
        }

        var candidates = _active.Values
            .Where(state => orderIndex.ContainsKey(state.Symbol) && state.DepthRequestId.HasValue)
            .OrderBy(state => state.TickByTickRequestId.HasValue ? 0 : 1)
            .ThenByDescending(state => GetActivityKeyDescending(state))
            .ThenBy(state => orderIndex[state.Symbol])
            .Select(state => state.Symbol)
            .Take(maxSymbols)
            .ToList();

        return candidates;
    }

    private static DateTimeOffset GetActivityKey(SubscriptionState state)
    {
        if (state.LastActivityUtc != DateTimeOffset.MinValue)
        {
            return state.LastActivityUtc;
        }

        return state.LastSeenUtc == DateTimeOffset.MinValue ? state.SubscribedAtUtc : state.LastSeenUtc;
    }

    private static DateTimeOffset GetActivityKeyDescending(SubscriptionState state)
    {
        return GetActivityKey(state);
    }

    private List<SubscriptionState> GetEvictionCandidates(
        HashSet<string> universeSet,
        TimeSpan minHold,
        bool allowBeforeHold,
        DateTimeOffset now)
    {
        var notInUniverse = _active.Values
            .Where(state => !universeSet.Contains(state.Symbol))
            .ToList();
        var inUniverse = _active.Values
            .Where(state => universeSet.Contains(state.Symbol))
            .ToList();

        var candidates = new List<SubscriptionState>();
        candidates.AddRange(SelectEligible(notInUniverse, minHold, now));
        if (allowBeforeHold)
        {
            candidates.AddRange(SelectIneligible(notInUniverse, minHold, now));
        }

        if (candidates.Count == 0 || allowBeforeHold)
        {
            candidates.AddRange(SelectEligible(inUniverse, minHold, now));
            if (allowBeforeHold)
            {
                candidates.AddRange(SelectIneligible(inUniverse, minHold, now));
            }
        }

        return candidates;
    }

    private static IEnumerable<SubscriptionState> SelectEligible(
        IEnumerable<SubscriptionState> states,
        TimeSpan minHold,
        DateTimeOffset now)
    {
        return states
            .Where(state => now - state.SubscribedAtUtc >= minHold)
            .OrderBy(state => GetActivityKey(state));
    }

    private static IEnumerable<SubscriptionState> SelectIneligible(
        IEnumerable<SubscriptionState> states,
        TimeSpan minHold,
        DateTimeOffset now)
    {
        return states
            .Where(state => now - state.SubscribedAtUtc < minHold)
            .OrderBy(state => GetActivityKey(state));
    }

    private bool IsDepthDisabled(string symbol, DateTimeOffset now)
    {
        if (_depthDisabledUntil.TryGetValue(symbol, out var until))
        {
            if (until > now)
            {
                return true;
            }

            _depthDisabledUntil.TryRemove(symbol, out _);
        }

        return false;
    }

    private bool IsTickByTickDisabled(string symbol, DateTimeOffset now)
    {
        if (_tickByTickDisabledUntil.TryGetValue(symbol, out var until))
        {
            if (until > now)
            {
                return true;
            }

            _tickByTickDisabledUntil.TryRemove(symbol, out _);
        }

        return false;
    }

    private void TrackRequests(SubscriptionState state)
    {
        TrackRequest(state.MktDataRequestId, state.Symbol, MarketDataRequestKind.MktData);
        TrackRequest(state.DepthRequestId, state.Symbol, MarketDataRequestKind.Depth);
        TrackRequest(state.TickByTickRequestId, state.Symbol, MarketDataRequestKind.TickByTick);
    }

    private void TrackRequest(int? requestId, string symbol, MarketDataRequestKind kind)
    {
        if (!requestId.HasValue)
        {
            return;
        }

        _requestMap[requestId.Value] = new RequestMapping(symbol, kind);
    }

    private void UntrackRequest(int requestId)
    {
        _requestMap.TryRemove(requestId, out _);
    }

    private void RemoveState(string symbol)
    {
        if (!_active.TryRemove(symbol, out var state))
        {
            return;
        }

        if (state.MktDataRequestId.HasValue)
        {
            UntrackRequest(state.MktDataRequestId.Value);
        }
        if (state.DepthRequestId.HasValue)
        {
            UntrackRequest(state.DepthRequestId.Value);
        }
        if (state.TickByTickRequestId.HasValue)
        {
            UntrackRequest(state.TickByTickRequestId.Value);
        }
    }

    private static List<string> NormalizeUniverse(IReadOnlyList<string> universe)
    {
        var normalized = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var symbol in universe)
        {
            if (string.IsNullOrWhiteSpace(symbol))
            {
                continue;
            }

            var trimmed = symbol.Trim().ToUpperInvariant();
            if (seen.Add(trimmed))
            {
                normalized.Add(trimmed);
            }
        }

        return normalized;
    }

    private sealed class SubscriptionState
    {
        public SubscriptionState(
            string symbol,
            int? mktDataRequestId,
            int? depthRequestId,
            int? tickByTickRequestId,
            DateTimeOffset subscribedAtUtc,
            DateTimeOffset lastSeenUtc)
        {
            Symbol = symbol;
            MktDataRequestId = mktDataRequestId;
            DepthRequestId = depthRequestId;
            TickByTickRequestId = tickByTickRequestId;
            SubscribedAtUtc = subscribedAtUtc;
            LastSeenUtc = lastSeenUtc;
            LastActivityUtc = DateTimeOffset.MinValue;
        }

        public string Symbol { get; }
        public int? MktDataRequestId { get; set; }
        public int? DepthRequestId { get; set; }
        public int? TickByTickRequestId { get; set; }
        public bool DepthUpdateReceived { get; set; }
        public bool DepthRetryAttempted { get; set; }
        public string? DepthExchange { get; set; }
        public DateTimeOffset SubscribedAtUtc { get; }
        public DateTimeOffset LastSeenUtc { get; set; }
        public DateTimeOffset LastActivityUtc { get; set; }
    }

    private sealed record RequestMapping(string Symbol, MarketDataRequestKind Kind);

    public void RecordDepthSubscribeAttempt(string symbol)
    {
        Interlocked.Increment(ref _depthSubscribeAttempts);
        _logger.LogInformation("[MarketData] Depth subscribe attempt symbol={Symbol}", symbol);
    }

    public void RecordDepthSubscribeUpdateReceived(int requestId)
    {
        if (!_requestMap.TryGetValue(requestId, out var mapping) || mapping.Kind != MarketDataRequestKind.Depth)
        {
            return;
        }

        if (!_active.TryGetValue(mapping.Symbol, out var state))
        {
            return;
        }

        if (state.DepthUpdateReceived)
        {
            return;
        }

        state.DepthUpdateReceived = true;
        Interlocked.Increment(ref _depthSubscribeUpdateReceived);
        _logger.LogInformation("[MarketData] Depth update received symbol={Symbol} depthId={DepthId}", mapping.Symbol, requestId);
    }

    public void RecordDepthSubscribeFailure(int? errorCode, string? errorMessage)
    {
        Interlocked.Increment(ref _depthSubscribeErrors);
        if (errorCode.HasValue)
        {
            _depthSubscribeErrorsByCode.AddOrUpdate(errorCode.Value, 1, (_, count) => count + 1);
        }
        lock (_depthDiagnosticsLock)
        {
            _lastDepthErrorCode = errorCode;
            _lastDepthErrorMessage = errorMessage;
        }

        if (errorCode.HasValue)
        {
            _logger.LogDebug("[MarketData] Depth subscribe failure code={Code} msg={Msg}", errorCode, errorMessage);
        }
        else
        {
            _logger.LogDebug("[MarketData] Depth subscribe failure msg={Msg}", errorMessage);
        }
    }

    public bool TryGetRequestMapping(int requestId, out string symbol, out MarketDataRequestKind kind)
    {
        if (_requestMap.TryGetValue(requestId, out var mapping))
        {
            symbol = mapping.Symbol;
            kind = mapping.Kind;
            return true;
        }

        symbol = string.Empty;
        kind = MarketDataRequestKind.MktData;
        return false;
    }

    public void RecordDepthRequestMetadata(string symbol, int requestId, string exchange)
    {
        if (!_active.TryGetValue(symbol, out var state))
        {
            return;
        }

        state.DepthRequestId = requestId;
        state.DepthUpdateReceived = false;
        state.DepthExchange = exchange;
        TrackRequest(requestId, symbol, MarketDataRequestKind.Depth);
    }

    public void ClearDepthRequest(string symbol, int requestId)
    {
        if (!_active.TryGetValue(symbol, out var state))
        {
            return;
        }

        if (state.DepthRequestId == requestId)
        {
            state.DepthRequestId = null;
            state.DepthUpdateReceived = false;
        }

        UntrackRequest(requestId);
    }

    public void UpdateDepthRequest(string symbol, int requestId, string exchange)
    {
        if (!_active.TryGetValue(symbol, out var state))
        {
            return;
        }

        state.DepthRequestId = requestId;
        state.DepthUpdateReceived = false;
        state.DepthExchange = exchange;
        TrackRequest(requestId, symbol, MarketDataRequestKind.Depth);
    }

    public async Task<DepthRetryPlan?> TryGetDepthRetryPlanAsync(int requestId, CancellationToken cancellationToken)
    {
        if (!_requestMap.TryGetValue(requestId, out var mapping) || mapping.Kind != MarketDataRequestKind.Depth)
        {
            return null;
        }

        if (!_active.TryGetValue(mapping.Symbol, out var state))
        {
            return null;
        }

        if (state.DepthRetryAttempted)
        {
            return null;
        }

        if (!string.Equals(state.DepthExchange, "SMART", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var classifications = await _classificationService.GetClassificationsAsync(new[] { mapping.Symbol }, cancellationToken);
        classifications.TryGetValue(mapping.Symbol, out var classification);
        if (classification is null || classification.ConId <= 0 || string.IsNullOrWhiteSpace(classification.PrimaryExchange))
        {
            return null;
        }

        state.DepthRetryAttempted = true;
        return new DepthRetryPlan(
            mapping.Symbol,
            classification.ConId,
            classification.SecType ?? "STK",
            classification.PrimaryExchange,
            classification.Currency,
            state.DepthExchange ?? "SMART");
    }

    public void MarkDepthUnsupported(string symbol, string reason, DateTimeOffset now)
    {
        _depthDisabledUntil[symbol] = now.Add(DepthCooldown);
        var classification = _classificationService.TryGetCached(symbol);
        _depthEligibilityCache.MarkIneligible(classification, symbol, reason, now.Add(DepthCooldown));
    }
}
