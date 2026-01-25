namespace RamStockAlerts.Execution.Risk;

using RamStockAlerts.Execution.Contracts;
using RamStockAlerts.Execution.Interfaces;

/// <summary>
/// Default risk manager implementation with comprehensive safety rails:
/// - Kill switch (emergency stop)
/// - Daily order/bracket limits
/// - Max open positions
/// - Per-symbol cooldowns
/// - Minimum time between orders globally
/// - Max notional per trade
/// - Max daily loss (placeholder for F6)
/// </summary>
public class RiskManagerV0 : IRiskManager
{
    private readonly ExecutionOptions _options;
    private readonly IOrderStateTracker? _orderTracker;
    private readonly decimal _maxNotionalUsd;
    private readonly decimal _maxShares;

    /// <summary>
    /// Create a new RiskManagerV0 with options and legacy limits.
    /// </summary>
    /// <param name="options">Execution options from configuration.</param>
    /// <param name="maxNotionalUsd">Legacy maximum notional USD per order (default 2000).</param>
    /// <param name="maxShares">Legacy maximum shares per order (default 500).</param>
    public RiskManagerV0(
        ExecutionOptions options,
        IOrderStateTracker? orderTracker = null,
        decimal maxNotionalUsd = 2000m,
        decimal maxShares = 500m)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _orderTracker = orderTracker;
        _maxNotionalUsd = maxNotionalUsd;
        _maxShares = maxShares;
    }

    /// <summary>
    /// Validate a single order intent against all risk rules.
    /// </summary>
    public RiskDecision Validate(
        OrderIntent intent,
        IExecutionLedger? ledger = null,
        DateTimeOffset? now = null)
    {
        // 1. CHECK KILL SWITCH
        if (_options.KillSwitch)
        {
            return RiskDecision.Reject(
                "Kill switch is active",
                new List<string> { "KillSwitch" });
        }

        // 2. CHECK SYMBOL
        if (string.IsNullOrWhiteSpace(intent.Symbol))
        {
            return RiskDecision.Reject("Symbol is required");
        }

        // 3. CHECK SIZING
        if (intent.Quantity is null && intent.NotionalUsd is null)
        {
            return RiskDecision.Reject("Either Quantity or NotionalUsd must be specified");
        }

        // Validate sizing conservatively
        if (intent.Quantity is not null && intent.Quantity > _maxShares)
        {
            return RiskDecision.Reject($"Quantity {intent.Quantity} exceeds maximum {_maxShares}");
        }
        if (intent.NotionalUsd is not null && intent.NotionalUsd > _maxNotionalUsd)
        {
            return RiskDecision.Reject($"Notional {intent.NotionalUsd} exceeds maximum {_maxNotionalUsd}");
        }

        // 4. CHECK LEDGER-BASED LIMITS (if provided)
        if (ledger is not null)
        {
            var now_utc = now ?? DateTimeOffset.UtcNow;

            // Daily order limit
            var orders_today = CountOrdersToday(ledger, now_utc);
            if (orders_today >= _options.MaxOrdersPerDay)
            {
                return RiskDecision.Reject(
                    $"Daily order limit ({_options.MaxOrdersPerDay}) reached",
                    new List<string> { "DailyOrderLimit" });
            }

            // Per-symbol cooldown
            var last_symbol_order = GetLastOrderForSymbol(ledger, intent.Symbol, now_utc);
            if (last_symbol_order is not null)
            {
                var seconds_since = (now_utc - last_symbol_order.CreatedUtc).TotalSeconds;
                if (seconds_since < _options.SymbolCooldownSeconds)
                {
                    return RiskDecision.Reject(
                        $"Symbol '{intent.Symbol}' on cooldown ({seconds_since:F1}s / {_options.SymbolCooldownSeconds}s)",
                        new List<string> { "SymbolCooldown" });
                }
            }

            // Min time between all orders
            var last_order = GetLastOrder(ledger, now_utc);
            if (last_order is not null)
            {
                var seconds_since = (now_utc - last_order.CreatedUtc).TotalSeconds;
                if (seconds_since < _options.MinSecondsBetweenOrders)
                {
                    return RiskDecision.Reject(
                        $"Min time between orders not met ({seconds_since:F1}s / {_options.MinSecondsBetweenOrders}s)",
                        new List<string> { "MinTimeBetweenOrders" });
                }
            }

            // Max open positions (counted as active brackets)
            var open_positions = CountOpenPositions(ledger, now_utc);
            if (open_positions >= _options.MaxOpenPositions)
            {
                return RiskDecision.Reject(
                    $"Max open positions ({_options.MaxOpenPositions}) reached",
                    new List<string> { "MaxOpenPositions" });
            }
        }

        // 5. CHECK DAILY LOSS LIMIT (if order tracker available)
        if (_orderTracker is not null)
        {
            var realizedPnlToday = _orderTracker.GetRealizedPnlToday();
            if (realizedPnlToday < 0 && Math.Abs(realizedPnlToday) >= _options.MaxLossPerDayUsd)
            {
                return RiskDecision.Reject(
                    $"Daily loss limit (${_options.MaxLossPerDayUsd}) reached. Current P&L: ${realizedPnlToday:F2}",
                    new List<string> { "DailyLossLimit" });
            }

            var estimatedMaxLoss = EstimateMaxLoss(intent);
            if (realizedPnlToday - estimatedMaxLoss < -_options.MaxLossPerDayUsd)
            {
                return RiskDecision.Reject(
                    $"Order would breach daily loss limit. Current P&L: ${realizedPnlToday:F2}, Est. max loss: ${estimatedMaxLoss:F2}",
                    new List<string> { "DailyLossLimitPreventive" });
            }
        }

        // 6. LIVE MODE SAFETY CHECKS
        if (_options.Live)
        {
            // Future: add account equity checks, max notional % validation, etc.
        }

        return RiskDecision.Allow();
    }

    /// <summary>
    /// Validate a bracket intent against all risk rules.
    /// </summary>
    public RiskDecision Validate(
        BracketIntent intent,
        IExecutionLedger? ledger = null,
        DateTimeOffset? now = null)
    {
        // 1. CHECK KILL SWITCH
        if (_options.KillSwitch)
        {
            return RiskDecision.Reject(
                "Kill switch is active",
                new List<string> { "KillSwitch" });
        }

        // 2. Validate entry order first
        var entry_validation = Validate(intent.Entry, ledger, now);
        if (!entry_validation.Allowed)
        {
            return entry_validation;
        }

        // 3. Require StopLoss in Live mode
        if (_options.Live)
        {
            if (intent.StopLoss is null)
            {
                return RiskDecision.Reject(
                    "StopLoss is required in Live mode",
                    new List<string> { "RequiredStopLossLive" });
            }
        }

        // 4. Validate StopLoss if present
        if (intent.StopLoss is not null)
        {
            var stop_validation = Validate(intent.StopLoss, ledger, now);
            if (!stop_validation.Allowed)
            {
                return RiskDecision.Reject(
                    $"StopLoss validation failed: {stop_validation.Reason}",
                    stop_validation.Tags);
            }
        }

        // 5. Validate TakeProfit if present
        if (intent.TakeProfit is not null)
        {
            var tp_validation = Validate(intent.TakeProfit, ledger, now);
            if (!tp_validation.Allowed)
            {
                return RiskDecision.Reject(
                    $"TakeProfit validation failed: {tp_validation.Reason}",
                    tp_validation.Tags);
            }
        }

        // 6. CHECK DAILY BRACKET LIMIT (if ledger provided)
        if (ledger is not null)
        {
            var now_utc = now ?? DateTimeOffset.UtcNow;
            var brackets_today = CountBracketsToday(ledger, now_utc);
            if (brackets_today >= _options.MaxBracketsPerDay)
            {
                return RiskDecision.Reject(
                    $"Daily bracket limit ({_options.MaxBracketsPerDay}) reached",
                    new List<string> { "DailyBracketLimit" });
            }
        }

        // 7. CHECK DAILY LOSS LIMIT (if order tracker available)
        if (_orderTracker is not null && intent.Entry is not null && intent.StopLoss is not null)
        {
            var realizedPnlToday = _orderTracker.GetRealizedPnlToday();
            var estimatedMaxLoss = EstimateBracketMaxLoss(intent);
            if (realizedPnlToday - estimatedMaxLoss < -_options.MaxLossPerDayUsd)
            {
                return RiskDecision.Reject(
                    $"Bracket would breach daily loss limit. Current P&L: ${realizedPnlToday:F2}, Est. max loss: ${estimatedMaxLoss:F2}",
                    new List<string> { "DailyLossLimitPreventive" });
            }
        }

        return RiskDecision.Allow();
    }

    // ========== HELPER METHODS FOR LEDGER QUERIES ==========

    private int CountOrdersToday(IExecutionLedger ledger, DateTimeOffset now)
    {
        return ledger.GetSubmittedIntentCountToday(now);
    }

    private int CountBracketsToday(IExecutionLedger ledger, DateTimeOffset now)
    {
        return ledger.GetSubmittedBracketCountToday(now);
    }

    private OrderIntent? GetLastOrder(IExecutionLedger ledger, DateTimeOffset now)
    {
        return ledger.GetIntents()
            .OrderByDescending(o => o.CreatedUtc)
            .FirstOrDefault();
    }

    private OrderIntent? GetLastOrderForSymbol(IExecutionLedger ledger, string symbol, DateTimeOffset now)
    {
        return ledger.GetIntents()
            .Where(o => o.Symbol == symbol)
            .OrderByDescending(o => o.CreatedUtc)
            .FirstOrDefault();
    }

    private int CountOpenPositions(IExecutionLedger ledger, DateTimeOffset now)
    {
        return ledger.GetOpenBracketCount();
    }

    private static decimal EstimateMaxLoss(OrderIntent intent)
    {
        if (intent.StopPrice is null || intent.LimitPrice is null)
        {
            return 0m;
        }

        var qty = intent.Quantity ??
                  (intent.NotionalUsd.HasValue && intent.LimitPrice > 0
                      ? Math.Floor(intent.NotionalUsd.Value / intent.LimitPrice.Value)
                      : 0m);

        var riskPerShare = Math.Abs(intent.LimitPrice.Value - intent.StopPrice.Value);
        return qty * riskPerShare;
    }

    private static decimal EstimateBracketMaxLoss(BracketIntent intent)
    {
        var entry = intent.Entry;
        var stop = intent.StopLoss;

        if (entry?.LimitPrice is null || stop?.StopPrice is null)
        {
            return 0m;
        }

        var qty = entry.Quantity ??
                  (entry.NotionalUsd.HasValue && entry.LimitPrice > 0
                      ? Math.Floor(entry.NotionalUsd.Value / entry.LimitPrice.Value)
                      : 0m);

        var riskPerShare = Math.Abs(entry.LimitPrice.Value - stop.StopPrice.Value);
        return qty * riskPerShare;
    }
}
