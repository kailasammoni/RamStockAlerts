using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using System.Collections.Concurrent;

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
    int? TickByTickRequestId);

public sealed record SubscriptionStats(int TotalSubscriptions, int DepthEnabled, int TickByTickEnabled);

public sealed class MarketDataSubscriptionManager
{
    private static readonly TimeSpan DepthCooldown = TimeSpan.FromDays(1);
    private static readonly TimeSpan TickByTickCooldown = TimeSpan.FromMinutes(30);

    private readonly IConfiguration _configuration;
    private readonly ILogger<MarketDataSubscriptionManager> _logger;
    private readonly SemaphoreSlim _sync = new(1, 1);
    private readonly ConcurrentDictionary<string, SubscriptionState> _active = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<int, RequestMapping> _requestMap = new();
    private readonly ConcurrentDictionary<string, DateTimeOffset> _depthDisabledUntil = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, DateTimeOffset> _tickByTickDisabledUntil = new(StringComparer.OrdinalIgnoreCase);

    public MarketDataSubscriptionManager(IConfiguration configuration, ILogger<MarketDataSubscriptionManager> logger)
    {
        _configuration = configuration;
        _logger = logger;
        var maxLines = _configuration.GetValue("MarketData:MaxLines", 95);
        var tickByTickMaxSymbols = _configuration.GetValue("MarketData:TickByTickMaxSymbols", 10);
        var depthRows = _configuration.GetValue("MarketData:DepthRows", 10);
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

        return new SubscriptionStats(_active.Count, depth, tick);
    }

    public IReadOnlyList<string> GetTickByTickSymbols()
    {
        return _active.Values
            .Where(state => state.TickByTickRequestId.HasValue)
            .Select(state => state.Symbol)
            .OrderBy(symbol => symbol)
            .ToList();
    }

    public bool IsFocusSymbol(string symbol)
    {
        if (string.IsNullOrWhiteSpace(symbol))
        {
            return false;
        }

        return _active.TryGetValue(symbol.Trim().ToUpperInvariant(), out var state)
            && state.TickByTickRequestId.HasValue;
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
        var normalizedUniverse = NormalizeUniverse(universe);
        var universeSet = new HashSet<string>(normalizedUniverse, StringComparer.OrdinalIgnoreCase);

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

        var now = DateTimeOffset.UtcNow;

        await _sync.WaitAsync(cancellationToken);
        try
        {
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

                var requestDepth = enableDepth && !IsDepthDisabled(symbol, now);
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
        CancellationToken cancellationToken)
    {
        if (errorCode != 10092 && errorCode != 10190)
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
        if (errorCode == 10092 && mapping.Kind == MarketDataRequestKind.Depth)
        {
            if (IsDepthDisabled(symbol, now))
            {
                return;
            }

            _depthDisabledUntil[symbol] = now.Add(DepthCooldown);

            if (state.DepthRequestId.HasValue)
            {
                await disableDepthAsync(symbol, cancellationToken);
                UntrackRequest(state.DepthRequestId.Value);
                state.DepthRequestId = null;
            }

            _logger.LogInformation(
                "DisableDepth symbol={Symbol} reason=DepthUnsupported",
                symbol);
            return;
        }

        if (errorCode == 10190 && mapping.Kind == MarketDataRequestKind.TickByTick)
        {
            if (IsTickByTickDisabled(symbol, now))
            {
                return;
            }

            _tickByTickDisabledUntil[symbol] = now.Add(TickByTickCooldown);

            if (state.TickByTickRequestId.HasValue)
            {
                await disableTickByTickAsync(symbol, cancellationToken);
                UntrackRequest(state.TickByTickRequestId.Value);
                state.TickByTickRequestId = null;
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

    private async Task<int> ApplyTickByTickAsync(
        List<string> focusSet,
        int tickByTickMaxSymbols,
        int maxLines,
        Func<string, CancellationToken, Task<int?>> enableTickByTickAsync,
        Func<string, CancellationToken, Task<bool>> disableTickByTickAsync,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
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
                    UntrackRequest(state.TickByTickRequestId.Value);
                    state.TickByTickRequestId = null;
                }
            }
        }

        var activeTickByTick = _active.Values.Count(state => state.TickByTickRequestId.HasValue);
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
            .Where(state => orderIndex.ContainsKey(state.Symbol))
            .OrderByDescending(state => GetActivityKeyDescending(state))
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
        public DateTimeOffset SubscribedAtUtc { get; }
        public DateTimeOffset LastSeenUtc { get; set; }
        public DateTimeOffset LastActivityUtc { get; set; }
    }

    private sealed record RequestMapping(string Symbol, MarketDataRequestKind Kind);
}
