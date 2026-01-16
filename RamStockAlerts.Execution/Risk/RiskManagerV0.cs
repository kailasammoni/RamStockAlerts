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
        decimal maxNotionalUsd = 2000m,
        decimal maxShares = 500m)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
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

        // 5. LIVE MODE SAFETY CHECKS
        if (intent.Mode == TradingMode.Live)
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
        if (intent.Entry.Mode == TradingMode.Live)
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

        return RiskDecision.Allow();
    }

    // ========== HELPER METHODS FOR LEDGER QUERIES ==========

    private int CountOrdersToday(IExecutionLedger ledger, DateTimeOffset now)
    {
        var today_start = now.Date;
        var today_end = today_start.AddDays(1);
        return ledger.GetIntents()
            .Count(o => o.CreatedUtc >= today_start && o.CreatedUtc < today_end);
    }

    private int CountBracketsToday(IExecutionLedger ledger, DateTimeOffset now)
    {
        var today_start = now.Date;
        var today_end = today_start.AddDays(1);
        return ledger.GetBrackets()
            .Count(b => b.Entry.CreatedUtc >= today_start && b.Entry.CreatedUtc < today_end);
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
        // Simplified: count active brackets (not yet closed/cancelled)
        // F6 will track order status properly; for now, assume all brackets are open
        var today_start = now.Date;
        var today_end = today_start.AddDays(1);
        return ledger.GetBrackets()
            .Count(b => b.Entry.CreatedUtc >= today_start && b.Entry.CreatedUtc < today_end);
    }
}
