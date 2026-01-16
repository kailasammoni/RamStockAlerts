using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using RamStockAlerts.Engine;
using RamStockAlerts.Models;
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
    string? DepthExchange,
    string? MktDataExchange = null,
    string? TickByTickExchange = null,
    DateTimeOffset? MktDataFirstReceiptMs = null,
    DateTimeOffset? TickByTickFirstReceiptMs = null);

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
    string PreviousExchange,
    string NextExchange);

public enum FocusExitReason
{
    None = 0,
    TapeNotWarmedUpTimeout = 1,  // Warmup criteria not met after grace period
    TapeStale = 2,               // No tape receipt for StaleWindowMs
    DepthStale = 3,              // No depth receipt for DepthStaleWindowMs
    WindowExpired = 4            // Evaluation window timeout reached
}

public sealed class MarketDataSubscriptionManager
{
    private static readonly TimeSpan DepthCooldown = TimeSpan.FromDays(1);
    private static readonly TimeSpan TickByTickCooldown = TimeSpan.FromMinutes(30);

    private readonly IConfiguration _configuration;
    private readonly ILogger<MarketDataSubscriptionManager> _logger;
    private readonly ContractClassificationService _classificationService;
    private readonly DepthEligibilityCache _depthEligibilityCache;
    private readonly OrderFlowMetrics _metrics;
    private readonly SemaphoreSlim _sync = new(1, 1);
    private readonly IShadowTradeJournal? _journal;
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
    private readonly List<DepthIneligibleEntry> _lastDepthIneligibleSymbols = new();
    private const int LastDepthIneligibleSymbolsLimit = 5;
    private readonly ConcurrentDictionary<int, DateTimeOffset> _pendingTickByTickCancels = new();
    private static readonly TimeSpan PendingCancelTtl = TimeSpan.FromMinutes(2);
    private bool _skipTickByTickEnableThisCycle;
    private bool _tickByTickCapLogged;
    private readonly HashSet<string> _activeUniverse = new(StringComparer.OrdinalIgnoreCase);
    private IReadOnlyCollection<string> _lastActiveUniverseSnapshot = Array.Empty<string>();
    private string _lastTriageLog = string.Empty;

    // Focus rotation defaults (ms). Configurable via MarketData:Focus* settings.
    private const int DefaultFocusMinDwellMs = 120_000;    // 2 minutes
    private const int DefaultFocusTapeIdleMs = 30_000;     // 30 seconds
    private const int DefaultFocusDepthIdleMs = 10_000;    // 10 seconds
    private const int DefaultFocusWarmupMinTrades = 3;
    private const int DefaultMinScoreDeltaToSwap = 15;
    
    // Early exit thresholds (ms). Configurable via MarketData:FocusEarlyExit* settings.
    private const int DefaultFocusWarmupGraceMs = 5_000;       // Grace period after warmup window starts
    private const int DefaultFocusEarlyExitStaleMs = 25_000;   // Tape/depth stale threshold for early exit
    private const int DefaultFocusDeadSymbolCooldownMs = 60_000; // Cooldown after dead tape (1 min)
    
    // Cooldown applied after normal window exit (longer than dead symbol)
    private static readonly TimeSpan FocusExitCooldown = TimeSpan.FromMinutes(5);

    public MarketDataSubscriptionManager(
        IConfiguration configuration,
        ILogger<MarketDataSubscriptionManager> logger,
        ContractClassificationService classificationService,
        DepthEligibilityCache depthEligibilityCache,
        OrderFlowMetrics metrics,
        IShadowTradeJournal? journal = null)
    {
        _configuration = configuration;
        _logger = logger;
        _classificationService = classificationService;
        _depthEligibilityCache = depthEligibilityCache;
        _metrics = metrics;
        _journal = journal;
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
        return _active.Values
            .Where(state => state.MktDataRequestId.HasValue)
            .Select(state => state.Symbol)
            .OrderBy(symbol => symbol)
            .ToList();
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
            && state.MktDataRequestId.HasValue;
    }

    public bool IsPremiumTapeEnabled(string symbol)
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

    /// <summary>
    /// Returns true if symbol is in the Active Universe.
    /// Active means: tape enabled, depth enabled, tick-by-tick enabled, and tape status == Ready.
    /// </summary>
    public bool IsActiveSymbol(string symbol)
    {
        if (string.IsNullOrWhiteSpace(symbol))
        {
            return false;
        }

        return _activeUniverse.Contains(symbol.Trim().ToUpperInvariant());
    }

    /// <summary>
    /// Returns an immutable snapshot of the current Active Universe.
    /// </summary>
    public IReadOnlyCollection<string> GetActiveUniverseSnapshot()
    {
        lock (_activeUniverse)
        {
            return _activeUniverse.OrderBy(s => s).ToList();
        }
    }

    /// <summary>
    /// Updates the Active Universe. Should only be called by subscription/universe refresh flow.
    /// Logs changes with counts and reasons.
    /// </summary>
    public void SetActiveUniverse(IEnumerable<string> symbols, string reason)
    {
        if (symbols is null)
        {
            throw new ArgumentNullException(nameof(symbols));
        }

        if (string.IsNullOrWhiteSpace(reason))
        {
            throw new ArgumentException("Reason cannot be null or empty", nameof(reason));
        }

        var normalized = symbols
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .Select(s => s.Trim().ToUpperInvariant())
            .OrderBy(s => s)
            .ToList();

        lock (_activeUniverse)
        {
            var added = normalized.Except(_activeUniverse, StringComparer.OrdinalIgnoreCase).ToList();
            var removed = _activeUniverse.Except(normalized, StringComparer.OrdinalIgnoreCase).ToList();

            _activeUniverse.Clear();
            foreach (var symbol in normalized)
            {
                _activeUniverse.Add(symbol);
            }

            _lastActiveUniverseSnapshot = normalized;

            if (added.Count > 0 || removed.Count > 0)
            {
                _logger.LogInformation(
                    "[ActiveUniverse] Update reason={Reason} active={ActiveCount} added={AddedCount} removed={RemovedCount} added=[{Added}] removed=[{Removed}]",
                    reason,
                    _activeUniverse.Count,
                    added.Count,
                    removed.Count,
                    string.Join(",", added),
                    string.Join(",", removed));
            }
            else
            {
                _logger.LogDebug(
                    "[ActiveUniverse] No change reason={Reason} active={ActiveCount}",
                    reason,
                    _activeUniverse.Count);
            }
        }
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

    /// <summary>
    /// Record a tape receipt (receipt-time based) for focus rotation telemetry.
    /// </summary>
    public void RecordTapeReceipt(string symbol, long receiptTimestampMs)
    {
        if (string.IsNullOrWhiteSpace(symbol))
        {
            return;
        }

        if (_active.TryGetValue(symbol.Trim().ToUpperInvariant(), out var state))
        {
            state.LastTapeReceiptMs = Math.Max(state.LastTapeReceiptMs, receiptTimestampMs);
            if (state.DepthRequestId.HasValue && state.FocusStartMs > 0)
            {
                state.TradesReceivedInDwell++;
            }
        }
    }

    /// <summary>
    /// Record a depth receipt (receipt-time based) for focus rotation telemetry.
    /// </summary>
    public void RecordDepthReceipt(string symbol, long receiptTimestampMs)
    {
        if (string.IsNullOrWhiteSpace(symbol))
        {
            return;
        }

        if (_active.TryGetValue(symbol.Trim().ToUpperInvariant(), out var state))
        {
            state.LastDepthReceiptMs = Math.Max(state.LastDepthReceiptMs, receiptTimestampMs);
            if (state.DepthRequestId.HasValue && state.FocusStartMs > 0)
            {
                state.DepthUpdatesInDwell++;
            }
        }
    }

    public async Task ApplyUniverseAsync(
        IReadOnlyList<string> candidates,
        Func<string, bool, CancellationToken, Task<MarketDataSubscription?>> subscribeAsync,
        Func<string, CancellationToken, Task<bool>> unsubscribeAsync,
        Func<string, CancellationToken, Task<int?>> enableTickByTickAsync,
        Func<string, CancellationToken, Task<bool>> disableTickByTickAsync,
        Func<string, CancellationToken, Task<bool>> disableDepthAsync,
        CancellationToken cancellationToken)
    {
        var universeSize = universe.Count;
        // Candidate Model: The input 'candidates' represents all symbols eligible for consideration.
        // ActiveUniverse will be computed as the strict subset with tape + depth + tick-by-tick subscriptions.
        
        var maxActiveSymbols = Math.Max(0, _configuration.GetValue("Universe:MaxActiveSymbols", 0));
        if (maxActiveSymbols > 0 && candidates.Count > maxActiveSymbols)
        {
            candidates = candidates.Take(maxActiveSymbols).ToList();
        }

        var normalizedCandidates = NormalizeUniverse(candidates);
        var candidatesSet = new HashSet<string>(normalizedCandidates, StringComparer.OrdinalIgnoreCase);
        var classifications = await _classificationService.GetClassificationsAsync(normalizedCandidates, cancellationToken);

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
        var maxDepthSymbols = Math.Max(0, _configuration.GetValue("MarketData:MaxDepthSymbols", 3));
        
        var now = DateTimeOffset.UtcNow;

        await _sync.WaitAsync(cancellationToken);
        try
        {
            _skipTickByTickEnableThisCycle = false;
            _lastUniverse = normalizedCandidates;
            _lastMaxLines = maxLines;
            var tickByTickMaxSymbols = _configuration.GetValue("MarketData:TickByTickMaxSymbols", 6);
            _lastTickByTickMaxSymbols = tickByTickMaxSymbols;

            foreach (var symbol in normalizedCandidates)
            {
                if (_active.TryGetValue(symbol, out var state))
                {
                    state.LastSeenUtc = now;
                    if (state.DepthRequestId.HasValue && state.FocusStartMs == 0)
                    {
                        state.FocusStartMs = state.SubscribedAtUtc.ToUnixTimeMilliseconds();
                    }
                }
            }

            // Focus rotation config
            var focusEnabled = _configuration.GetValue("MarketData:FocusRotationEnabled", true);
            var focusMinDwellMs = _configuration.GetValue("MarketData:FocusMinDwellMs", DefaultFocusMinDwellMs);
            var focusTapeIdleMs = _configuration.GetValue("MarketData:FocusTapeIdleMs", DefaultFocusTapeIdleMs);
            var focusDepthIdleMs = _configuration.GetValue("MarketData:FocusDepthIdleMs", DefaultFocusDepthIdleMs);
            var focusWarmupMinTrades = _configuration.GetValue("MarketData:FocusWarmupMinTrades", DefaultFocusWarmupMinTrades);
            var minScoreDeltaToSwap = _configuration.GetValue("MarketData:MinScoreDeltaToSwap", DefaultMinScoreDeltaToSwap);

            var triageScores = ComputeTriageScores(normalizedCandidates, now);
            LogTriageScoresIfChanged(triageScores);

            // Phase 1: Clean up depth subscriptions for symbols no longer in depth candidate set (after focus rotation)
            var focusSelection = SelectDepthCandidates(
                triageScores,
                maxDepthSymbols,
                now,
                focusEnabled,
                focusMinDwellMs,
                focusTapeIdleMs,
                focusDepthIdleMs,
                focusWarmupMinTrades,
                minScoreDeltaToSwap);
            var depthCandidates = focusSelection.DepthCandidates;
            var depthCandidatesSet = new HashSet<string>(depthCandidates, StringComparer.OrdinalIgnoreCase);
            var focusEvictions = focusSelection.FocusEvictions;
            
            foreach (var state in _active.Values)
            {
                if (state.DepthRequestId.HasValue && !depthCandidatesSet.Contains(state.Symbol))
                {
                    // This symbol had depth but is no longer in the depth candidate set
                    if (await disableDepthAsync(state.Symbol, cancellationToken))
                    {
                        UntrackRequest(state.DepthRequestId!.Value);
                        state.DepthRequestId = null;
                        ResetFocusTelemetry(state);
                        var reason = focusEvictions.TryGetValue(state.Symbol, out var r)
                            ? r
                            : "NotInDepthCandidateSet";
                        _logger.LogInformation(
                            "[MarketData] Removed depth for {Symbol} reason={Reason}",
                            state.Symbol,
                            reason);
                    }
                    
                    // Also remove tick-by-tick since it's only for depth symbols
                    if (state.TickByTickRequestId.HasValue && await disableTickByTickAsync(state.Symbol, cancellationToken))
                    {
                        MarkPendingCancel(state.TickByTickRequestId.Value, now);
                        state.TickByTickRequestId = null;
                        _logger.LogInformation(
                            "[MarketData] Removed tick-by-tick for {Symbol} reason=DepthRemoved",
                            state.Symbol);
                    }
                }
            }

            // Phase 2: Free lines if over cap
            var totalLines = GetTotalLines();
            if (totalLines > maxLines)
            {
                var linesToFree = totalLines - maxLines;
                linesToFree -= await FreeLinesByDroppingTickByTickAsync(
                    linesToFree,
                    candidatesSet,
                    disableTickByTickAsync,
                    now,
                    cancellationToken);

                if (linesToFree > 0)
                {
                    await EvictForLinesAsync(
                        linesToFree,
                        candidatesSet,
                        minHold,
                        allowBeforeHold: true,
                        unsubscribeAsync,
                        cancellationToken,
                        "over cap");
                }
            }

            // Phase 3: Subscribe tape-only for all candidates that don't have active subscriptions
            foreach (var symbol in normalizedCandidates)
            {
                if (_active.ContainsKey(symbol))
                {
                    continue;
                }

                if (!enableTape)
                {
                    continue;
                }

                totalLines = GetTotalLines();
                if (totalLines + 1 > maxLines)
                {
                    var linesToFree = (totalLines + 1) - maxLines;
                    linesToFree -= await FreeLinesByDroppingTickByTickAsync(
                        linesToFree,
                        candidatesSet,
                        disableTickByTickAsync,
                        now,
                        cancellationToken);

                    if (linesToFree > 0)
                    {
                        var freed = await EvictForLinesAsync(
                            linesToFree,
                            candidatesSet,
                            minHold,
                            allowBeforeHold: false,
                            unsubscribeAsync,
                            cancellationToken,
                            "make room");

                        if (freed <= 0 || GetTotalLines() + 1 > maxLines)
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

                // Subscribe tape-only (requestDepth = false)
                var subscription = await subscribeAsync(symbol, false, cancellationToken);
                if (subscription is null)
                {
                    _logger.LogWarning("[MarketData] Subscribe failed for {Symbol}.", symbol);
                    continue;
                }

                var state = new SubscriptionState(
                    subscription.Symbol,
                    subscription.MktDataRequestId,
                    null, // No depth yet
                    null, // No tick-by-tick yet
                    now,
                    now);
                state.DepthExchange = subscription.DepthExchange;
                RecordDepthAttempt(state, subscription.DepthExchange);
                _active[subscription.Symbol] = state;
                TrackRequest(subscription.MktDataRequestId, subscription.Symbol, MarketDataRequestKind.MktData);

                _logger.LogInformation(
                    "[MarketData] Subscribed tape-only {Symbol} mktDataId={MktDataId} activeLines={ActiveLines}/{MaxLines}",
                    subscription.Symbol,
                    subscription.MktDataRequestId,
                    GetTotalLines(),
                    maxLines);
            }

            // Phase 4: Enable depth for depth candidates
            if (enableDepth)
            {
                _logger.LogInformation(
                    "[MarketData] Phase 4: Depth loop starting - depthCandidates count={Count}, symbols=[{Symbols}]",
                    depthCandidates.Count,
                    string.Join(",", depthCandidates));

                if (depthCandidates.Count > 0)
                {
                    _logger.LogInformation("[MarketData] Depth candidates selected: {Symbols}", string.Join(",", depthCandidates));
                }

                foreach (var symbol in depthCandidates)
                {
                    if (!_active.TryGetValue(symbol, out var state))
                    {
                        continue; // Symbol not subscribed yet
                    }

                    if (state.DepthRequestId.HasValue)
                    {
                        continue; // Already has depth
                    }

                    totalLines = GetTotalLines();
                    if (totalLines + 1 > maxLines)
                    {
                        _logger.LogInformation(
                            "[MarketData] Cannot add depth for {Symbol}: at line cap ({ActiveLines}/{MaxLines})",
                            symbol,
                            GetTotalLines(),
                            maxLines);
                        continue;
                    }

                    classifications.TryGetValue(symbol, out var classification);
                    if (!_depthEligibilityCache.CanRequestDepth(classification, symbol, now, out var eligibilityState))
                    {
                        _depthEligibilityCache.LogSkipOnce(classification, symbol, eligibilityState);
                        continue;
                    }

                    // Upgrade to depth (from tape-only) for this symbol
                    _logger.LogInformation("[MarketData] Upgrading to depth for {Symbol}", symbol);
                    RecordDepthSubscribeAttempt(symbol);
                    var depthSubscription = await subscribeAsync(symbol, true, cancellationToken);
                    if (depthSubscription?.DepthRequestId != null)
                    {
                        state.DepthRequestId = depthSubscription.DepthRequestId;
                        state.DepthExchange = depthSubscription.DepthExchange;
                        StartFocusWindow(state, now.ToUnixTimeMilliseconds());
                        TrackRequest(depthSubscription.DepthRequestId.Value, symbol, MarketDataRequestKind.Depth);
                        _logger.LogInformation(
                            "[MarketData] Enabled depth for {Symbol} depthId={DepthId} activeLines={ActiveLines}/{MaxLines}",
                            symbol,
                            depthSubscription.DepthRequestId,
                            GetTotalLines(),
                            maxLines);
                    }
                }
            }

            // Phase 5: Enable tick-by-tick for all depth symbols (required for ActiveUniverse)
            await ApplyTickByTickForDepthSymbolsAsync(
                enableTickByTickAsync,
                disableTickByTickAsync,
                disableDepthAsync,
                maxLines,
                now,
                cancellationToken);

            LogTapeDepthPairingIfChanged(maxDepthSymbols);
            LogTapeGateConfigIfChanged();
            LogDepthEligibilitySummary(universeSize, normalizedUniverse, classifications, now);
            LogDepthEligibilitySummary(normalizedCandidates, classifications, now);
            
            // Log detailed universe refresh snapshot with focus rotation state
            LogUniverseRefreshSnapshot(normalizedCandidates, triageScores, now);
            
            // Update ActiveUniverse: only symbols with all required subscriptions are Active
            // Note: Tape status check (Ready) will be done at evaluation time by strategy
            UpdateActiveUniverseAfterSubscriptionChanges("UniverseRefresh");
            
            // Log post-Phase 4/5 subscription state
            var depthEnabledSymbols = GetDepthEnabledSymbols();
            var premiumTapeSymbols = _active.Values
                .Where(state => state.TickByTickRequestId.HasValue)
                .Select(state => state.Symbol)
                .OrderBy(s => s)
                .ToList();
            var activeUniverse = GetActiveUniverseSnapshot();
            
            _logger.LogInformation(
                "[MarketData] Post-Phase 4/5 state - depthCandidates=[{DepthCandidates}], depthEnabled=[{DepthEnabled}], premiumTape=[{PremiumTape}], ActiveUniverse=[{ActiveUniverse}]",
                string.Join(",", depthCandidates),
                string.Join(",", depthEnabledSymbols),
                string.Join(",", premiumTapeSymbols),
                string.Join(",", activeUniverse));
            
            LogSubscriptionSummary(normalizedCandidates.Count);
            
                    // Emit UniverseUpdate journal entry for audit trail
                    EmitUniverseUpdateJournalEntry(normalizedCandidates, triageScores, now);
        }
        finally
        {
            _sync.Release();
        }
    }

    /// <summary>
    /// Emits a UniverseUpdate journal entry once per refresh cycle for audit purposes.
    /// </summary>
    private void EmitUniverseUpdateJournalEntry(
        IReadOnlyList<string> candidates,
        IReadOnlyList<TriageScore> triageScores,
        DateTimeOffset now)
    {
        if (_journal == null)
        {
            return;
        }

        var nowMs = now.ToUnixTimeMilliseconds();
        var activeSnapshot = GetActiveUniverseSnapshot();
        var triageScoreLookup = triageScores.ToDictionary(t => t.Symbol, t => t, StringComparer.OrdinalIgnoreCase);
        var tapeGateConfig = ShadowTradingHelpers.ReadTapeGateConfig(_configuration);

        // Collect counts
        var tapeCount = _active.Values.Count(state => state.MktDataRequestId.HasValue);
        var depthCount = _active.Values.Count(state => state.DepthRequestId.HasValue);
        var tickByTickCount = _active.Values.Count(state => state.TickByTickRequestId.HasValue);
        
        // Build ActiveSymbolDetail for each active symbol with diagnostics
        var activeSymbols = new List<ShadowTradeJournalEntry.ActiveSymbolDetail>();
        foreach (var symbol in activeSnapshot)
        {
            var book = _metrics.GetOrderBookSnapshot(symbol);
            if (book == null)
            {
                // No book data available - add symbol with nulls for diagnostic fields
                activeSymbols.Add(new ShadowTradeJournalEntry.ActiveSymbolDetail
                {
                    Symbol = symbol,
                    LastTapeRecvAgeMs = null,
                    TradesInWarmupWindow = null,
                    WarmedUp = null,
                    LastDepthRecvAgeMs = null,
                    TriageScore = triageScoreLookup.TryGetValue(symbol, out var score) 
                        ? score.Score 
                        : null
                });
                continue;
            }
            
            var tapeStatus = ShadowTradingHelpers.GetTapeStatus(book, nowMs, IsTapeEnabled(symbol), tapeGateConfig);
            
            var detail = new ShadowTradeJournalEntry.ActiveSymbolDetail
            {
                Symbol = symbol,
                LastTapeRecvAgeMs = tapeStatus.AgeMs,
                TradesInWarmupWindow = tapeStatus.TradesInWarmupWindow,
                WarmedUp = tapeStatus.Kind == ShadowTradingHelpers.TapeStatusKind.Ready,
                LastDepthRecvAgeMs = book.LastDepthRecvMs.HasValue 
                    ? nowMs - book.LastDepthRecvMs.Value 
                    : null,
                TriageScore = triageScoreLookup.TryGetValue(symbol, out var score2) 
                    ? score2.Score 
                    : null
            };
            activeSymbols.Add(detail);
        }

        // Identify exclusions (symbols with tape but not in ActiveUniverse)
        var exclusions = new List<ShadowTradeJournalEntry.UniverseExclusion>();
        var activeSet = new HashSet<string>(activeSnapshot, StringComparer.OrdinalIgnoreCase);

        foreach (var state in _active.Values)
        {
            if (!activeSet.Contains(state.Symbol))
            {
                // Determine exclusion reason
                string reason;
                if (!state.DepthRequestId.HasValue)
                {
                    reason = "NoDepth";
                }
                else if (!state.TickByTickRequestId.HasValue)
                {
                    reason = "NoTickByTick";
                }
                else if (!state.MktDataRequestId.HasValue)
                {
                    reason = "NoTape";
                }
                else
                {
                    reason = "Unknown";
                }

                exclusions.Add(new ShadowTradeJournalEntry.UniverseExclusion
                {
                    Symbol = state.Symbol,
                    Reason = reason
                });
            }
        }

        var entry = new ShadowTradeJournalEntry
        {
            SchemaVersion = ShadowTradeJournal.CurrentSchemaVersion,
            SessionId = _journal.SessionId,
            EntryType = "UniverseUpdate",
            Source = "MarketDataSubscriptionManager",
            MarketTimestampUtc = now,
            UniverseUpdate = new ShadowTradeJournalEntry.UniverseUpdateSnapshot
            {
                SchemaVersion = 1,
                NowMs = nowMs,
                NowUtc = now,
                Candidates = candidates.Take(20).ToList(),
                ActiveSymbols = activeSymbols,
                Exclusions = exclusions,
                Counts = new ShadowTradeJournalEntry.UniverseCounts
                {
                    CandidatesCount = candidates.Count,
                    ActiveCount = activeSnapshot.Count,
                    DepthCount = depthCount,
                    TickByTickCount = tickByTickCount,
                    TapeCount = tapeCount
                }
            }
        };

        _journal.TryEnqueue(entry);
    }

    /// <summary>
    /// Computes and updates the Active Universe based on current subscription state.
    /// A symbol is Active IFF: tape enabled, depth enabled, and tick-by-tick enabled.
    /// Note: Tape activity gate (Ready status) is checked at strategy evaluation time, not here.
    /// </summary>
    private void UpdateActiveUniverseAfterSubscriptionChanges(string reason)
    {
        var activeSymbols = _active.Values
            .Where(state => 
                state.MktDataRequestId.HasValue &&  // Tape subscription
                state.DepthRequestId.HasValue &&     // Depth subscription
                state.TickByTickRequestId.HasValue)  // Tick-by-tick subscription
            .Select(state => state.Symbol)
            .ToList();

        SetActiveUniverse(activeSymbols, reason);
    }

    /// <summary>
    /// Enables tick-by-tick for all depth symbols. This is REQUIRED for ActiveUniverse membership.
    /// If tick-by-tick enable fails for a depth symbol, the depth subscription is removed and
    /// the symbol is excluded from ActiveUniverse.
    /// </summary>
    private async Task ApplyTickByTickForDepthSymbolsAsync(
        Func<string, CancellationToken, Task<int?>> enableTickByTickAsync,
        Func<string, CancellationToken, Task<bool>> disableTickByTickAsync,
        Func<string, CancellationToken, Task<bool>> disableDepthAsync,
        int maxLines,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        CleanupPendingCancels(now);

        if (_skipTickByTickEnableThisCycle)
        {
            // Tick-by-tick cap hit - clean up all depth subscriptions since they require tick-by-tick
            var depthSymbolsToClean = _active.Values
                .Where(state => state.DepthRequestId.HasValue)
                .ToList();
            
            foreach (var state in depthSymbolsToClean)
            {
                _logger.LogWarning(
                    "[MarketData] Removing depth for {Symbol} reason=TickByTickCapHit",
                    state.Symbol);
                
                if (await disableDepthAsync(state.Symbol, cancellationToken))
                {
                    UntrackRequest(state.DepthRequestId!.Value);
                    state.DepthRequestId = null;
                    ResetFocusTelemetry(state);
                }
            }
            return;
        }

        var depthSymbols = _active.Values
            .Where(state => state.DepthRequestId.HasValue)
            .ToList();

        foreach (var state in depthSymbols)
        {
            if (state.TickByTickRequestId.HasValue)
            {
                continue; // Already has tick-by-tick
            }

            if (IsTickByTickDisabled(state.Symbol, now))
            {
                // Tick-by-tick is temporarily disabled due to cooldown
                // Remove depth since we require tick-by-tick for depth symbols
                _logger.LogWarning(
                    "[MarketData] Removing depth for {Symbol} reason=TickByTickCooldown",
                    state.Symbol);
                
                if (await disableDepthAsync(state.Symbol, cancellationToken))
                {
                    UntrackRequest(state.DepthRequestId!.Value);
                    state.DepthRequestId = null;
                    ResetFocusTelemetry(state);
                }
                continue;
            }

            if (GetTotalLines() + 1 > maxLines)
            {
                _logger.LogWarning(
                    "[MarketData] Cannot enable tick-by-tick for {Symbol}: at line cap ({ActiveLines}/{MaxLines}). Removing depth.",
                    state.Symbol,
                    GetTotalLines(),
                    maxLines);
                
                if (await disableDepthAsync(state.Symbol, cancellationToken))
                {
                    UntrackRequest(state.DepthRequestId!.Value);
                    state.DepthRequestId = null;
                    ResetFocusTelemetry(state);
                }
                continue;
            }

            var requestId = await enableTickByTickAsync(state.Symbol, cancellationToken);
            if (!requestId.HasValue)
            {
                _logger.LogWarning(
                    "[MarketData] Tick-by-tick enable failed for {Symbol}. Removing depth.",
                    state.Symbol);
                
                if (await disableDepthAsync(state.Symbol, cancellationToken))
                {
                    UntrackRequest(state.DepthRequestId!.Value);
                    state.DepthRequestId = null;
                    ResetFocusTelemetry(state);
                }
                continue;
            }

            state.TickByTickRequestId = requestId.Value;
            state.LastActivityUtc = now;
            TrackRequest(requestId.Value, state.Symbol, MarketDataRequestKind.TickByTick);
            
            _logger.LogInformation(
                "[MarketData] Enabled tick-by-tick for {Symbol} tickByTickId={TickByTickId} activeLines={ActiveLines}/{MaxLines}",
                state.Symbol,
                requestId.Value,
                GetTotalLines(),
                maxLines);
        }
    }

    /// <summary>
    /// Logs a detailed summary of the universe refresh showing focus rotation state.
    /// Structured for debuggability: one line captures CandidatesCount, TapeOnlyCount, FocusCount, FocusSymbols,
    /// and for each focus symbol: FocusAgeMs, LastTapeRecvAgeMs, LastDepthRecvAgeMs, TriageScore.
    /// </summary>
    private void LogUniverseRefreshSnapshot(
        IReadOnlyList<string> candidates,
        List<TriageScore> triageScores,
        DateTimeOffset now)
    {
        var nowMs = now.ToUnixTimeMilliseconds();
        var focusSymbols = _active.Values
            .Where(state => state.DepthRequestId.HasValue && state.FocusStartMs > 0)
            .OrderBy(state => state.Symbol)
            .ToList();
        
        var focusCount = focusSymbols.Count;
        var tapeOnlyCount = _active.Values.Count(state => 
            state.MktDataRequestId.HasValue && !state.DepthRequestId.HasValue);
        
        var scoreMap = triageScores.ToDictionary(ts => ts.Symbol, ts => ts.Score);
        
        var focusDetails = string.Join(
            "|",
            focusSymbols.Select(state =>
            {
                var focusAgeMs = nowMs - state.FocusStartMs;
                var lastTapeRecvAgeMs = state.LastTapeReceiptMs > 0 ? nowMs - state.LastTapeReceiptMs : -1;
                var lastDepthRecvAgeMs = state.LastDepthReceiptMs > 0 ? nowMs - state.LastDepthReceiptMs : -1;
                var triageScore = scoreMap.TryGetValue(state.Symbol, out var score) ? (int)score : 0;
                
                return $"{state.Symbol}(age={focusAgeMs}ms,tape={lastTapeRecvAgeMs}ms,depth={lastDepthRecvAgeMs}ms,score={triageScore})";
            }));

        _logger.LogInformation(
            "[UniverseRefresh] Snapshot: candidates={CandidatesCount} tapeOnly={TapeOnlyCount} focus={FocusCount} focusSymbols=[{FocusDetails}]",
            candidates.Count,
            tapeOnlyCount,
            focusCount,
            focusDetails);
    }

    /// <summary>
    /// Logs a summary of subscription counts to verify the candidate model invariants.
    /// Invariant: depthCount == activeCount (since tick-by-tick is required for depth).
    /// </summary>
    private void LogSubscriptionSummary(int candidatesCount)
    {
        var tapeCount = _active.Values.Count(state => state.MktDataRequestId.HasValue);
        var depthCount = _active.Values.Count(state => state.DepthRequestId.HasValue);
        var tickByTickCount = _active.Values.Count(state => state.TickByTickRequestId.HasValue);
        var activeCount = _activeUniverse.Count;

        _logger.LogInformation(
            "[MarketData] Subscription summary: candidates={CandidatesCount} tape={TapeCount} depth={DepthCount} tickByTick={TickByTickCount} active={ActiveCount}",
            candidatesCount,
            tapeCount,
            depthCount,
            tickByTickCount,
            activeCount);

        // Verify invariant: depth should equal active (since tick-by-tick is required for depth)
        if (depthCount != activeCount)
        {
            _logger.LogWarning(
                "[MarketData] Invariant violation: depthCount ({DepthCount}) != activeCount ({ActiveCount}). This indicates incomplete subscriptions.",
                depthCount,
                activeCount);
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

            // Remove tick-by-tick subscription
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

            // Tick-by-tick is REQUIRED for ActiveUniverse membership.
            // Remove depth subscription since depth without tick-by-tick is not allowed for Active symbols.
            if (state.DepthRequestId.HasValue && await disableDepthAsync(symbol, cancellationToken))
            {
                UntrackRequest(state.DepthRequestId.Value);
                state.DepthRequestId = null;
                ResetFocusTelemetry(state);
            }

            // Update ActiveUniverse - this symbol is now excluded
            UpdateActiveUniverseAfterSubscriptionChanges("TickByTickUnavailable");

            _logger.LogInformation(
                "[MarketData] ActiveUniverse exclude {Symbol} reason=TickByTickUnavailable",
                symbol);
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
                    "DepthIneligible: symbol={Symbol} code={Code} msg={Msg} -> removing from depth eligibility. Will be reconsidered on next universe refresh.",
                    symbol,
                    errorCode,
                    errorMessage);
            }

            EnqueueDepthIneligibleSymbol(symbol, now);

            MarkDepthUnsupported(symbol, $"DepthUnsupported:{errorCode}", now);
            _logger.LogWarning(
                "DepthUnsupportedConfirmed: symbol={Symbol} attempts=[{Attempts}]",
                symbol,
                string.Join(",", GetDepthAttempts(state)));

            if (state.DepthRequestId.HasValue && await disableDepthAsync(symbol, cancellationToken))
            {
                UntrackRequest(state.DepthRequestId.Value);
                state.DepthRequestId = null;
                ResetFocusTelemetry(state);
            }

            if (state.TickByTickRequestId.HasValue && await disableTickByTickAsync(symbol, cancellationToken))
            {
                MarkPendingCancel(state.TickByTickRequestId.Value, now);
                state.TickByTickRequestId = null;
            }

            // Update ActiveUniverse since this symbol lost depth + tick-by-tick
            UpdateActiveUniverseAfterSubscriptionChanges("DepthIneligible");
            
            LogTapeDepthPairingIfChanged(_lastTickByTickMaxSymbols);
        }
        finally
        {
            _sync.Release();
        }
    }

    private void EnqueueDepthIneligibleSymbol(string symbol, DateTimeOffset timestamp)
    {
        if (string.IsNullOrWhiteSpace(symbol))
        {
            return;
        }

        var normalized = symbol.Trim().ToUpperInvariant();
        var existingIndex = _lastDepthIneligibleSymbols.FindIndex(entry =>
            entry.Symbol.Equals(normalized, StringComparison.OrdinalIgnoreCase));
        if (existingIndex >= 0)
        {
            _lastDepthIneligibleSymbols.RemoveAt(existingIndex);
        }

        _lastDepthIneligibleSymbols.Add(new DepthIneligibleEntry(normalized, timestamp));
        while (_lastDepthIneligibleSymbols.Count > LastDepthIneligibleSymbolsLimit)
        {
            _lastDepthIneligibleSymbols.RemoveAt(0);
        }
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

    private void LogDepthEligibilitySummary(
        int universeSize,
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
            "DepthEligibilitySummary: universeSize={UniverseSize} depthEnabledCount={DepthEnabled} depthUnsupportedCount={DepthUnsupported} last10092Symbols=[{LastSymbols}]",
            universeSize,
            depthEnabled,
            depthUnsupported,
            string.Join(",", _lastDepthIneligibleSymbols.Select(entry => entry.ToString())));
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

    private List<TriageScore> ComputeTriageScores(IReadOnlyList<string> candidates, DateTimeOffset now)
    {
        var nowMs = now.ToUnixTimeMilliseconds();
        var scores = new List<TriageScore>(candidates.Count);

        // Configuration for eligibility
        var triageLookbackMs = _configuration.GetValue("MarketData:TriageLookbackMs", 15_000);
        var maxSpreadPct = _configuration.GetValue("MarketData:TriageMaxSpreadPct", 0.05m);
        var minTradesInWindow = _configuration.GetValue("MarketData:TriageMinTradesInWindow", 1);

        for (var i = 0; i < candidates.Count; i++)
        {
            var symbol = candidates[i];
            var book = _metrics.GetOrderBookSnapshot(symbol);
            if (book is null)
            {
                var deadMetrics = new ProbeMetrics(
                    LastL1RecvMs: -1,
                    LastTapeRecvMs: -1,
                    TradesInLast15s: 0,
                    QuoteUpdatesInLast15s: 0,
                    LastSpread: null,
                    IsEligible: false);
                scores.Add(new TriageScore(symbol, -100m, 0m, 0m, 0m, null, 0m, 0m, i, deadMetrics, false));
                continue;
            }

            var trades = book.RecentTrades.ToArray();
            var windowStart = nowMs - triageLookbackMs;
            var tradesInWindow = trades.Where(t => t.ReceiptTimestampMs >= windowStart).ToList();

            // Track metrics for diagnostics
            var lastL1RecvMs = book.LastL1RecvMs ?? -1;
            var lastTapeRecvMs = tradesInWindow.Count > 0 ? tradesInWindow.Max(t => t.ReceiptTimestampMs) : -1;
            var quoteUpdatesInWindow = Math.Max(0, (int)((nowMs - (lastL1RecvMs > 0 ? lastL1RecvMs : nowMs - triageLookbackMs)) / 100m));
            
            var metrics = new ProbeMetrics(
                LastL1RecvMs: lastL1RecvMs,
                LastTapeRecvMs: lastTapeRecvMs,
                TradesInLast15s: tradesInWindow.Count,
                QuoteUpdatesInLast15s: quoteUpdatesInWindow,
                LastSpread: book.Spread > 0m ? book.Spread : null,
                IsEligible: false); // Will be set below

            // PRECONDITION 1: Must have at least 1 tape print in lookback window
            if (tradesInWindow.Count < minTradesInWindow)
            {
                var eligibleMetrics = metrics with { IsEligible = false };
                scores.Add(new TriageScore(symbol, -100m, 0m, 0m, 0m, null, 0m, 0m, i, eligibleMetrics, false));
                continue;
            }

            // PRECONDITION 2: Spread must be known and under max
            decimal? spread = book.Spread > 0m ? book.Spread : null;
            var mid = book.BestBid > 0 && book.BestAsk > 0 ? (book.BestBid + book.BestAsk) / 2 : 0m;
            if (mid == 0m && tradesInWindow.Count > 0)
            {
                mid = (decimal)tradesInWindow.Average(t => t.Price);
            }

            if (spread is null || mid <= 0m)
            {
                var eligibleMetrics = metrics with { IsEligible = false };
                scores.Add(new TriageScore(symbol, -90m, 0m, 0m, 0m, null, 0m, 0m, i, eligibleMetrics, false));
                continue;
            }

            var spreadPct = (spread.Value / mid);
            if (spreadPct > maxSpreadPct)
            {
                // Heavy penalty for wide spread
                var eligibleMetrics = metrics with { IsEligible = false };
                scores.Add(new TriageScore(symbol, -80m, 0m, 0m, 0m, spread, 0m, 0m, i, eligibleMetrics, false));
                continue;
            }

            // Symbol is eligible - now compute regular scoring
            var window3Start = nowMs - 3_000;
            var window15Start = nowMs - 15_000;
            var trades3 = trades.Where(t => t.ReceiptTimestampMs >= window3Start).ToList();
            var trades15 = trades.Where(t => t.ReceiptTimestampMs >= window15Start).ToList();

            var rate3s = trades3.Count / 3m;
            var rate15s = trades15.Count / 15m;
            var dollarVol15s = trades15.Sum(t => (decimal)t.Price * t.Size);

            decimal volatilityRangePct = 0m;
            if (mid > 0m && trades15.Count > 0)
            {
                var minPx = trades15.Min(t => (decimal)t.Price);
                var maxPx = trades15.Max(t => (decimal)t.Price);
                volatilityRangePct = (maxPx - minPx) / mid;
            }

            var rate15Baseline = rate15s <= 0m ? 0.1m : rate15s;
            var burst = rate3s / rate15Baseline;

            var rate3Score = Clamp01(rate3s / 5m);
            var rate15Score = Clamp01(rate15s / 2m);
            var dollarScore = Clamp01((decimal)Math.Log10((double)(1m + dollarVol15s)) / 4m);
            var spreadScore = spread.HasValue
                ? Clamp01((0.02m - (spread.Value / mid)) / 0.02m)
                : 0.5m;
            var volatilityScore = Clamp01(volatilityRangePct / 0.005m); // saturate around 50 bps move
            var burstScore = Clamp01(burst / 3m); // saturate around 3x acceleration

            var score = 100m * (
                0.25m * rate3Score +
                0.15m * rate15Score +
                0.20m * dollarScore +
                0.10m * spreadScore +
                0.15m * volatilityScore +
                0.15m * burstScore);

            var eligibleMetrics2 = metrics with { IsEligible = true };
            scores.Add(new TriageScore(
                symbol,
                Math.Round(score, 1),
                Math.Round(rate3s, 2),
                Math.Round(rate15s, 2),
                Math.Round(dollarVol15s, 2),
                spread,
                Math.Round(volatilityRangePct, 4),
                Math.Round(burst, 2),
                i,
                eligibleMetrics2,
                true));
        }

        return scores
            .OrderByDescending(s => s.Score)
            .ThenBy(s => s.CandidateOrder)
            .ToList();
    }

    private void LogTriageScoresIfChanged(IReadOnlyList<TriageScore> triageScores)
    {
        if (triageScores.Count == 0)
        {
            return;
        }

        var top = triageScores
            .Take(10)
            .Select(s =>
            {
                var eligMarker = s.IsEligible ? "✓" : "✗";
                var tapeInfo = s.Metrics.TradesInLast15s > 0 ? $"trades={s.Metrics.TradesInLast15s}" : "NO_TAPE";
                return $"{s.Symbol}:{s.Score:F1}({eligMarker}|{tapeInfo}|t3={s.Rate3s:F2}/s t15={s.Rate15s:F2}/s dv={s.DollarVol15s:F0} burst={s.Burst:F2} spread={(s.Spread ?? 0m):F4})";
            });
        var logLine = string.Join(" | ", top);
        if (logLine.Equals(_lastTriageLog, StringComparison.Ordinal))
        {
            return;
        }

        _lastTriageLog = logLine;
        var eligibleCount = triageScores.Count(s => s.IsEligible);
        _logger.LogInformation("[MarketData] TriageTop10 eligible={EligibleCount}/{TotalCount} {Top}", eligibleCount, triageScores.Count, logLine);
    }

    private static decimal Clamp01(decimal value)
    {
        if (value < 0m)
        {
            return 0m;
        }

        if (value > 1m)
        {
            return 1m;
        }

        return value;
    }

    private sealed record ProbeMetrics(
        long LastL1RecvMs,
        long LastTapeRecvMs,
        int TradesInLast15s,
        int QuoteUpdatesInLast15s,
        decimal? LastSpread,
        bool IsEligible);

    private sealed record TriageScore(
        string Symbol,
        decimal Score,
        decimal Rate3s,
        decimal Rate15s,
        decimal DollarVol15s,
        decimal? Spread,
        decimal VolatilityRangePct,
        decimal Burst,
        int CandidateOrder,
        ProbeMetrics Metrics,
        bool IsEligible);

    private sealed record FocusSelectionResult(List<string> DepthCandidates, Dictionary<string, string> FocusEvictions);

    private sealed record FocusEvaluation(
        bool ShouldEvict,
        string Reason,
        long DwellMs,
        long? TapeAgeMs,
        long? DepthAgeMs,
        FocusExitReason ExitReason = FocusExitReason.None);

    /// <summary>
    /// Selects up to maxDepth symbols from candidates for depth (L2) subscriptions.
    /// Applies focus rotation to evict idle depth symbols after dwell and backfill with fresh candidates.
    /// Includes hysteresis to retain focus unless challenger beats by configured delta after dwell.
    /// </summary>
    private FocusSelectionResult SelectDepthCandidates(
        IReadOnlyList<TriageScore> triagedCandidates,
        int maxDepth,
        DateTimeOffset now,
        bool focusEnabled,
        int focusMinDwellMs,
        int focusTapeIdleMs,
        int focusDepthIdleMs,
        int focusWarmupMinTrades,
        int minScoreDeltaToSwap,
        int focusWarmupGraceMs = DefaultFocusWarmupGraceMs,
        int focusEarlyExitStaleMs = DefaultFocusEarlyExitStaleMs)
    {
        if (maxDepth <= 0 || triagedCandidates.Count == 0)
        {
            return new FocusSelectionResult(new List<string>(), new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase));
        }

        focusMinDwellMs = Math.Max(0, focusMinDwellMs);
        focusTapeIdleMs = Math.Max(0, focusTapeIdleMs);
        focusDepthIdleMs = Math.Max(0, focusDepthIdleMs);
        focusWarmupMinTrades = Math.Max(0, focusWarmupMinTrades);
        minScoreDeltaToSwap = Math.Max(0, minScoreDeltaToSwap);

        var triageBySymbol = triagedCandidates.ToDictionary(t => t.Symbol, StringComparer.OrdinalIgnoreCase);
        
        // Only candidates that are marked eligible can be promoted to depth slots
        var eligible = triagedCandidates
            .Where(t => t.IsEligible && !IsDepthDisabled(t.Symbol, now))
            .OrderByDescending(t => t.Score)
            .ThenBy(t => t.CandidateOrder)
            .ToList();

        if (eligible.Count == 0)
        {
            _logger.LogInformation("[MarketData] FocusCandidates selected: 0 (no eligible symbols - all dead or disabled)");
            return new FocusSelectionResult(new List<string>(), new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase));
        }

        if (!focusEnabled || eligible.Count <= maxDepth)
        {
            _logger.LogInformation("[MarketData] FocusCandidates selected: {Count} (from {TotalCandidates} candidates, {TotalEligible} eligible)", 
                Math.Min(eligible.Count, maxDepth), triagedCandidates.Count, eligible.Count);
            return new FocusSelectionResult(eligible.Take(maxDepth).Select(t => t.Symbol).ToList(), new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase));
        }

        var nowMs = now.ToUnixTimeMilliseconds();
        var focusEvictions = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        var focusStates = _active.Values
            .Where(state => state.DepthRequestId.HasValue)
            .ToList();
        var focusSet = new HashSet<string>(focusStates.Select(s => s.Symbol), StringComparer.OrdinalIgnoreCase);

        var keptFocus = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var state in focusStates)
        {
            if (!triageBySymbol.TryGetValue(state.Symbol, out var triage))
            {
                continue;
            }

            var evaluation = EvaluateFocus(state, nowMs, focusMinDwellMs, focusTapeIdleMs, focusDepthIdleMs, focusWarmupMinTrades, focusWarmupGraceMs, focusEarlyExitStaleMs);
            if (evaluation.ShouldEvict)
            {
                focusEvictions[state.Symbol] = evaluation.Reason;
                
                // Apply appropriate cooldown based on exit reason
                TimeSpan cooldown = FocusExitCooldown; // Default: 5 minutes for normal window exit
                if (evaluation.ExitReason == FocusExitReason.TapeNotWarmedUpTimeout || evaluation.ExitReason == FocusExitReason.TapeStale)
                {
                    // Short cooldown for dead tape symbols (can retry quickly)
                    var deadSymbolCooldownMs = Math.Max(0, _configuration.GetValue("MarketData:FocusDeadSymbolCooldownMs", DefaultFocusDeadSymbolCooldownMs));
                    cooldown = TimeSpan.FromMilliseconds(deadSymbolCooldownMs);
                }
                
                _logger.LogInformation(
                    "[MarketData] Focus rotation evicting {Symbol} reason={Reason} exitType={ExitType} dwellMs={DwellMs} trades={Trades} depthUpdates={DepthUpdates} tapeAgeMs={TapeAgeMs} depthAgeMs={DepthAgeMs} cooldownMs={CooldownMs}",
                    state.Symbol,
                    evaluation.Reason,
                    evaluation.ExitReason,
                    evaluation.DwellMs,
                    state.TradesReceivedInDwell,
                    state.DepthUpdatesInDwell,
                    evaluation.TapeAgeMs ?? -1,
                    evaluation.DepthAgeMs ?? -1,
                    (long)cooldown.TotalMilliseconds);
                
                // Apply depth cooldown
                _depthDisabledUntil[state.Symbol] = now.Add(cooldown);
                continue;
            }

            var dwellMet = state.FocusStartMs > 0 && nowMs - state.FocusStartMs >= focusMinDwellMs;
            var challenger = eligible.FirstOrDefault(t => !focusSet.Contains(t.Symbol) && !focusEvictions.ContainsKey(t.Symbol));
            if (dwellMet && challenger != null && challenger.Score >= triage.Score + minScoreDeltaToSwap)
            {
                focusEvictions[state.Symbol] = "HysteresisChallenger";
                _logger.LogInformation(
                    "[MarketData] Focus swap candidate challenger={Challenger} challengerScore={ChallengerScore:F1} focus={FocusSymbol} focusScore={FocusScore:F1} delta={Delta:F1} dwellMs={DwellMs}",
                    challenger.Symbol,
                    challenger.Score,
                    state.Symbol,
                    triage.Score,
                    challenger.Score - triage.Score,
                    nowMs - state.FocusStartMs);
                continue;
            }

            keptFocus.Add(state.Symbol);
        }

        var selection = new List<string>();

        foreach (var candidate in eligible)
        {
            if (selection.Count >= maxDepth)
            {
                break;
            }

            if (focusEvictions.ContainsKey(candidate.Symbol))
            {
                continue;
            }

            if (keptFocus.Contains(candidate.Symbol) && !selection.Contains(candidate.Symbol))
            {
                selection.Add(candidate.Symbol);
            }
        }

        if (selection.Count < maxDepth)
        {
            foreach (var candidate in eligible)
            {
                if (selection.Count >= maxDepth)
                {
                    break;
                }

                if (focusEvictions.ContainsKey(candidate.Symbol))
                {
                    continue;
                }

                if (selection.Contains(candidate.Symbol))
                {
                    continue;
                }

                selection.Add(candidate.Symbol);
            }
        }

        return new FocusSelectionResult(selection.Take(maxDepth).ToList(), focusEvictions);
    }

    private FocusEvaluation EvaluateFocus(
        SubscriptionState state,
        long nowMs,
        int focusMinDwellMs,
        int focusTapeIdleMs,
        int focusDepthIdleMs,
        int focusWarmupMinTrades,
        int focusWarmupGraceMs = DefaultFocusWarmupGraceMs,
        int focusEarlyExitStaleMs = DefaultFocusEarlyExitStaleMs)
    {
        var focusStartMs = state.FocusStartMs > 0 ? state.FocusStartMs : state.SubscribedAtUtc.ToUnixTimeMilliseconds();
        var dwellMs = Math.Max(0, nowMs - focusStartMs);
        var tapeAgeMs = state.LastTapeReceiptMs > 0 ? nowMs - state.LastTapeReceiptMs : (long?)null;
        var depthAgeMs = state.LastDepthReceiptMs > 0 ? nowMs - state.LastDepthReceiptMs : (long?)null;
        var dwellMet = dwellMs >= focusMinDwellMs;
        var tapeIdle = dwellMet && (!tapeAgeMs.HasValue || tapeAgeMs.Value >= focusTapeIdleMs);
        var depthIdle = dwellMet && (!depthAgeMs.HasValue || depthAgeMs.Value >= focusDepthIdleMs);
        var warmupMet = state.TradesReceivedInDwell >= focusWarmupMinTrades;

        // EARLY EXIT DETECTION (before dwell is met):
        // If tape fails to warm up even after grace period, evict immediately
        var warmupGraceMet = dwellMs >= (focusMinDwellMs + focusWarmupGraceMs);
        var warmupFailedWithGrace = warmupGraceMet && !warmupMet;
        var tapeStaleEarly = !dwellMet && tapeAgeMs.HasValue && tapeAgeMs.Value >= focusEarlyExitStaleMs;
        var depthStaleEarly = !dwellMet && depthAgeMs.HasValue && depthAgeMs.Value >= focusEarlyExitStaleMs;

        // Determine exit reason
        FocusExitReason exitReason = FocusExitReason.None;
        string reason;
        var shouldEvict = false;
        
        if (warmupFailedWithGrace && (tapeStaleEarly || depthStaleEarly))
        {
            exitReason = FocusExitReason.TapeNotWarmedUpTimeout;
            reason = "TapeNotWarmedUpTimeout";
            shouldEvict = true;
        }
        else if (tapeStaleEarly)
        {
            exitReason = FocusExitReason.TapeStale;
            reason = "TapeStaleEarly";
            shouldEvict = true;
        }
        else if (depthStaleEarly)
        {
            exitReason = FocusExitReason.DepthStale;
            reason = "DepthStaleEarly";
            shouldEvict = true;
        }
        else if (dwellMet && ((tapeIdle && depthIdle) || (!warmupMet && (tapeIdle || depthIdle))))
        {
            exitReason = FocusExitReason.WindowExpired;
            if (tapeIdle && depthIdle)
            {
                reason = "TapeDepthIdle";
            }
            else
            {
                // Given the outer condition and the failed (tapeIdle && depthIdle) check,
                // the only remaining reachable case is !warmupMet && (tapeIdle || depthIdle).
                reason = "WarmupNotMet";
            }
            shouldEvict = true;
        }
        else if (!dwellMet)
        {
            reason = "DwellNotMet";
        }
        else
        {
            reason = "Retain";
        }

        return new FocusEvaluation(shouldEvict, reason, dwellMs, tapeAgeMs, depthAgeMs, exitReason);
    }

    private static DateTimeOffset GetActivityKey(SubscriptionState state)
    {
        if (state.LastActivityUtc != DateTimeOffset.MinValue)
        {
            return state.LastActivityUtc;
        }

        return state.LastSeenUtc == DateTimeOffset.MinValue ? state.SubscribedAtUtc : state.LastSeenUtc;
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

    private static void ResetFocusTelemetry(SubscriptionState state)
    {
        state.FocusStartMs = 0;
        state.LastTapeReceiptMs = 0;
        state.LastDepthReceiptMs = 0;
        state.TradesReceivedInDwell = 0;
        state.DepthUpdatesInDwell = 0;
    }

    private static void StartFocusWindow(SubscriptionState state, long nowMs)
    {
        state.FocusStartMs = nowMs;
        state.TradesReceivedInDwell = 0;
        state.DepthUpdatesInDwell = 0;
        state.LastTapeReceiptMs = 0;
        state.LastDepthReceiptMs = 0;
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

    private sealed record DepthIneligibleEntry(string Symbol, DateTimeOffset Timestamp)
    {
        public override string ToString() => $"{Symbol}({Timestamp:O})";
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
            FocusStartMs = 0;
            LastTapeReceiptMs = 0;
            LastDepthReceiptMs = 0;
            TradesReceivedInDwell = 0;
            DepthUpdatesInDwell = 0;
        }

        public string Symbol { get; }
        public int? MktDataRequestId { get; set; }
        public int? DepthRequestId { get; set; }
        public int? TickByTickRequestId { get; set; }
        public bool DepthUpdateReceived { get; set; }
        public bool DepthRetryAttempted { get; set; }
        public string? DepthExchange { get; set; }
        public List<string> DepthAttemptedExchanges { get; } = new();
        public DateTimeOffset SubscribedAtUtc { get; }
        public DateTimeOffset LastSeenUtc { get; set; }
        public DateTimeOffset LastActivityUtc { get; set; }
        public long FocusStartMs { get; set; }
        public long LastTapeReceiptMs { get; set; }
        public long LastDepthReceiptMs { get; set; }
        public int TradesReceivedInDwell { get; set; }
        public int DepthUpdatesInDwell { get; set; }
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
        RecordDepthAttempt(state, exchange);
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
        RecordDepthAttempt(state, exchange);
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

        var classifications = await _classificationService.GetClassificationsAsync(new[] { mapping.Symbol }, cancellationToken);
        classifications.TryGetValue(mapping.Symbol, out var classification);
        if (classification is null || classification.ConId <= 0 ||
            (string.IsNullOrWhiteSpace(classification.PrimaryExchange) && string.IsNullOrWhiteSpace(classification.Exchange)))
        {
            return null;
        }

        var previousExchange = string.IsNullOrWhiteSpace(state.DepthExchange) ? "SMART" : state.DepthExchange!;
        var nextExchange = ResolveDepthRetryExchange(previousExchange, classification);
        if (string.IsNullOrWhiteSpace(nextExchange) ||
            string.Equals(previousExchange, nextExchange, StringComparison.OrdinalIgnoreCase) ||
            state.DepthAttemptedExchanges.Contains(nextExchange, StringComparer.OrdinalIgnoreCase))
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
            previousExchange,
            nextExchange);
    }

    public void MarkDepthUnsupported(string symbol, string reason, DateTimeOffset now)
    {
        _depthDisabledUntil[symbol] = now.Add(DepthCooldown);
        var classification = _classificationService.TryGetCached(symbol);
        _depthEligibilityCache.MarkIneligible(classification, symbol, reason, now.Add(DepthCooldown));
    }

    private static string? ResolveDepthRetryExchange(string previousExchange, ContractClassification classification)
    {
        if (string.Equals(previousExchange, "SMART", StringComparison.OrdinalIgnoreCase))
        {
            return !string.IsNullOrWhiteSpace(classification.PrimaryExchange)
                ? classification.PrimaryExchange
                : classification.Exchange;
        }

        return "SMART";
    }

    private static void RecordDepthAttempt(SubscriptionState state, string? exchange)
    {
        if (string.IsNullOrWhiteSpace(exchange))
        {
            return;
        }

        if (!state.DepthAttemptedExchanges.Contains(exchange, StringComparer.OrdinalIgnoreCase))
        {
            state.DepthAttemptedExchanges.Add(exchange);
        }
    }

    private static IReadOnlyList<string> GetDepthAttempts(SubscriptionState state)
    {
        if (state.DepthAttemptedExchanges.Count == 0 && !string.IsNullOrWhiteSpace(state.DepthExchange))
        {
            return new[] { state.DepthExchange! };
        }

        if (!string.IsNullOrWhiteSpace(state.DepthExchange) &&
            !state.DepthAttemptedExchanges.Contains(state.DepthExchange, StringComparer.OrdinalIgnoreCase))
        {
            return state.DepthAttemptedExchanges.Concat(new[] { state.DepthExchange! }).ToList();
        }

        return state.DepthAttemptedExchanges;
    }
}
