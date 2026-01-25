namespace RamStockAlerts.Execution.Tests;

using RamStockAlerts.Execution.Contracts;
using RamStockAlerts.Execution.Interfaces;
using RamStockAlerts.Execution.Risk;
using Xunit;

/// <summary>
/// Unit tests for RiskManagerV0 - kill switch, cooldowns, daily limits, and safety rails.
/// </summary>
public class RiskManagerV0Tests
{
    #region Fixture & Helpers

    private readonly ExecutionOptions _defaultOptions = new()
    {
        Enabled = true,
        KillSwitch = false,
        MaxOrdersPerDay = 20,
        MaxBracketsPerDay = 10,
        MaxOpenPositions = 3,
        MaxNotionalPerTradePct = 10m,
        MaxLossPerDayUsd = 200m,
        SymbolCooldownSeconds = 120,
        MinSecondsBetweenOrders = 10
    };

    private RiskManagerV0 CreateRiskManager(ExecutionOptions? options = null)
    {
        var opts = options ?? _defaultOptions;
        return new RiskManagerV0(opts, maxNotionalUsd: 2000m, maxShares: 500m);
    }

    private FakeExecutionLedger CreateLedger()
    {
        return new FakeExecutionLedger();
    }

    private OrderIntent CreateOrder(
        string symbol = "AAPL",
        decimal? quantity = 100m,
        DateTimeOffset? created = null)
    {
        return new OrderIntent
        {
            Symbol = symbol,
            Side = OrderSide.Buy,
            Type = OrderType.Market,
            Quantity = quantity,
            CreatedUtc = created ?? DateTimeOffset.UtcNow
        };
    }

    private BracketIntent CreateBracket(
        string symbol = "AAPL",
        decimal? quantity = 100m,
        DateTimeOffset? created = null)
    {
        var entry = CreateOrder(symbol, quantity, created);
        return new BracketIntent
        {
            Entry = entry,
            TakeProfit = new OrderIntent
            {
                Symbol = symbol,
                Side = OrderSide.Sell,
                Type = OrderType.Limit,
                Quantity = quantity
            },
            StopLoss = new OrderIntent
            {
                Symbol = symbol,
                Side = OrderSide.Sell,
                Type = OrderType.Stop,
                Quantity = quantity
            }
        };
    }

    #endregion

    #region Kill Switch Tests

    [Fact]
    public void Validate_Order_KillSwitch_Active_Rejects()
    {
        // Arrange
        var options = new ExecutionOptions { KillSwitch = true };
        var rm = CreateRiskManager(options);
        var order = CreateOrder();

        // Act
        var result = rm.Validate(order);

        // Assert
        Assert.False(result.Allowed);
        Assert.Contains("Kill switch", result.Reason);
        Assert.Contains("KillSwitch", result.Tags);
    }

    [Fact]
    public void Validate_Bracket_KillSwitch_Active_Rejects()
    {
        // Arrange
        var options = new ExecutionOptions { KillSwitch = true };
        var rm = CreateRiskManager(options);
        var bracket = CreateBracket();

        // Act
        var result = rm.Validate(bracket);

        // Assert
        Assert.False(result.Allowed);
        Assert.Contains("Kill switch", result.Reason);
        Assert.Contains("KillSwitch", result.Tags);
    }

    #endregion

    #region Basic Validation Tests

    [Fact]
    public void Validate_Order_NoSymbol_Rejects()
    {
        // Arrange
        var rm = CreateRiskManager();
        var order = new OrderIntent { Symbol = null, Quantity = 100m };

        // Act
        var result = rm.Validate(order);

        // Assert
        Assert.False(result.Allowed);
        Assert.Contains("Symbol", result.Reason);
    }

    [Fact]
    public void Validate_Order_NoSizing_Rejects()
    {
        // Arrange
        var rm = CreateRiskManager();
        var order = new OrderIntent { Symbol = "AAPL", Quantity = null, NotionalUsd = null };

        // Act
        var result = rm.Validate(order);

        // Assert
        Assert.False(result.Allowed);
        Assert.Contains("Quantity or NotionalUsd", result.Reason);
    }

    [Fact]
    public void Validate_Order_QuantityExceedsMax_Rejects()
    {
        // Arrange
        var rm = CreateRiskManager();
        var order = CreateOrder(quantity: 600m); // Max is 500

        // Act
        var result = rm.Validate(order);

        // Assert
        Assert.False(result.Allowed);
        Assert.Contains("exceeds maximum", result.Reason);
    }

    [Fact]
    public void Validate_Order_NotionalExceedsMax_Rejects()
    {
        // Arrange
        var rm = CreateRiskManager();
        var order = new OrderIntent
        {
            Symbol = "AAPL",
            Side = OrderSide.Buy,
            Type = OrderType.Market,
            NotionalUsd = 2500m // Max is 2000
        };

        // Act
        var result = rm.Validate(order);

        // Assert
        Assert.False(result.Allowed);
        Assert.Contains("exceeds maximum", result.Reason);
    }

    [Fact]
    public void Validate_Order_Valid_Allows()
    {
        // Arrange
        var rm = CreateRiskManager();
        var order = CreateOrder();

        // Act
        var result = rm.Validate(order);

        // Assert
        Assert.True(result.Allowed);
        Assert.Null(result.Reason);
    }

    #endregion

    #region Symbol Cooldown Tests

    [Fact]
    public void Validate_Order_SymbolCooldown_TooSoon_Rejects()
    {
        // Arrange
        var rm = CreateRiskManager();
        var ledger = CreateLedger();
        var now = DateTimeOffset.UtcNow;

        // First order on AAPL
        var order1 = CreateOrder("AAPL", created: now);
        ledger.RecordIntent(order1);

        // Second order on AAPL immediately after (within cooldown)
        var order2 = CreateOrder("AAPL", created: now.AddSeconds(30));

        // Act
        var result = rm.Validate(order2, ledger, now.AddSeconds(30));

        // Assert
        Assert.False(result.Allowed);
        Assert.Contains("cooldown", result.Reason!.ToLower());
        Assert.Contains("SymbolCooldown", result.Tags);
    }

    [Fact]
    public void Validate_Order_SymbolCooldown_AfterExpiry_Allows()
    {
        // Arrange
        var rm = CreateRiskManager();
        var ledger = CreateLedger();
        var now = DateTimeOffset.UtcNow;

        // First order on AAPL
        var order1 = CreateOrder("AAPL", created: now);
        ledger.RecordIntent(order1);

        // Second order on AAPL after cooldown expires
        var order2 = CreateOrder("AAPL", created: now.AddSeconds(120));

        // Act
        var result = rm.Validate(order2, ledger, now.AddSeconds(121));

        // Assert
        Assert.True(result.Allowed);
    }

    [Fact]
    public void Validate_Order_DifferentSymbol_NoCooldown_Allows()
    {
        // Arrange
        var rm = CreateRiskManager();
        var ledger = CreateLedger();
        var now = DateTimeOffset.UtcNow;

        // Order on AAPL
        var order1 = CreateOrder("AAPL", created: now);
        ledger.RecordIntent(order1);

        // Order on different symbol AFTER global min time (not just different symbol)
        var order2 = CreateOrder("MSFT", created: now.AddSeconds(15));

        // Act
        var result = rm.Validate(order2, ledger, now.AddSeconds(15));

        // Assert
        Assert.True(result.Allowed);
    }

    #endregion

    #region Min Time Between Orders Tests

    [Fact]
    public void Validate_Order_GlobalMinTime_TooSoon_Rejects()
    {
        // Arrange
        var rm = CreateRiskManager();
        var ledger = CreateLedger();
        var now = DateTimeOffset.UtcNow;

        // First order
        var order1 = CreateOrder("AAPL", created: now);
        ledger.RecordIntent(order1);

        // Second order within min time
        var order2 = CreateOrder("MSFT", created: now.AddSeconds(5));

        // Act
        var result = rm.Validate(order2, ledger, now.AddSeconds(5));

        // Assert
        Assert.False(result.Allowed);
        Assert.Contains("Min time between orders", result.Reason);
        Assert.Contains("MinTimeBetweenOrders", result.Tags);
    }

    [Fact]
    public void Validate_Order_GlobalMinTime_AfterExpiry_Allows()
    {
        // Arrange
        var rm = CreateRiskManager();
        var ledger = CreateLedger();
        var now = DateTimeOffset.UtcNow;

        // First order
        var order1 = CreateOrder("AAPL", created: now);
        ledger.RecordIntent(order1);

        // Second order after min time
        var order2 = CreateOrder("MSFT", created: now.AddSeconds(10));

        // Act
        var result = rm.Validate(order2, ledger, now.AddSeconds(11));

        // Assert
        Assert.True(result.Allowed);
    }

    #endregion

    #region Daily Order Limit Tests

    [Fact]
    public void Validate_Order_DailyLimit_Reached_Rejects()
    {
        // Arrange
        var options = new ExecutionOptions { MaxOrdersPerDay = 5 };
        var rm = CreateRiskManager(options);
        var ledger = CreateLedger();
        var now = DateTimeOffset.UtcNow;

        // Add 5 orders today (different symbols to avoid symbol cooldown)
        for (int i = 0; i < 5; i++)
        {
            var symbol = $"SYM{i}"; // Different symbol each time
            var order = CreateOrder(symbol, created: now.AddSeconds(i * 30)); // 30s apart (exceeds min time)
            ledger.RecordIntent(order);
            ledger.RecordResult(order.IntentId, new ExecutionResult
            {
                IntentId = order.IntentId,
                Status = ExecutionStatus.Submitted,
                TimestampUtc = order.CreatedUtc
            });
        }

        // Try to add 6th order (different symbol, after min time)
        var order6 = CreateOrder("SYM5", created: now.AddSeconds(5 * 30));

        // Act
        var result = rm.Validate(order6, ledger, now.AddSeconds(5 * 30));

        // Assert
        Assert.False(result.Allowed);
        Assert.Contains("Daily order limit", result.Reason);
        Assert.Contains("DailyOrderLimit", result.Tags);
    }

    [Fact]
    public void Validate_Order_DailyLimit_NotReached_Allows()
    {
        // Arrange
        var options = new ExecutionOptions { MaxOrdersPerDay = 20 };
        var rm = CreateRiskManager(options);
        var ledger = CreateLedger();
        var now = DateTimeOffset.UtcNow;

        // Add 10 orders
        for (int i = 0; i < 10; i++)
        {
            var order = CreateOrder(created: now.AddSeconds(i * 60));
            ledger.RecordIntent(order);
            ledger.RecordResult(order.IntentId, new ExecutionResult
            {
                IntentId = order.IntentId,
                Status = ExecutionStatus.Submitted,
                TimestampUtc = order.CreatedUtc
            });
        }

        // Try to add 11th order
        var order11 = CreateOrder(created: now.AddSeconds(10 * 60));

        // Act
        var result = rm.Validate(order11, ledger, now.AddSeconds(10 * 60 + 120));

        // Assert
        Assert.True(result.Allowed);
    }

    #endregion

    #region Max Open Positions Tests

    [Fact]
    public void Validate_Order_MaxOpenPositions_Reached_Rejects()
    {
        // Arrange
        var options = new ExecutionOptions { MaxOpenPositions = 2 };
        var rm = CreateRiskManager(options);
        var ledger = CreateLedger();
        var now = DateTimeOffset.UtcNow;

        // Add 2 brackets today (different symbols to avoid cooldown)
        for (int i = 0; i < 2; i++)
        {
            var symbol = $"SYM{i}";
            var bracket = CreateBracket(symbol, created: now.AddSeconds(i * 200)); // 200s apart
            ledger.RecordBracket(bracket);
            ledger.UpdateBracketState(bracket.Entry.IntentId, BracketState.Open);
        }

        // Try to add order (which would increase open positions, after min time and different symbol)
        var order = CreateOrder("SYM2", created: now.AddSeconds(2 * 200 + 30));

        // Act
        var result = rm.Validate(order, ledger, now.AddSeconds(2 * 200 + 30));

        // Assert
        Assert.False(result.Allowed);
        Assert.Contains("Max open positions", result.Reason);
        Assert.Contains("MaxOpenPositions", result.Tags);
    }

    [Fact]
    public void Validate_Order_MaxOpenPositions_NotReached_Allows()
    {
        // Arrange
        var options = new ExecutionOptions { MaxOpenPositions = 5 };
        var rm = CreateRiskManager(options);
        var ledger = CreateLedger();
        var now = DateTimeOffset.UtcNow;

        // Add 2 brackets
        for (int i = 0; i < 2; i++)
        {
            var bracket = CreateBracket(created: now.AddSeconds(i * 180));
            ledger.RecordBracket(bracket);
            ledger.UpdateBracketState(bracket.Entry.IntentId, BracketState.Open);
        }

        var order = CreateOrder(created: now.AddSeconds(2 * 180 + 120));

        // Act
        var result = rm.Validate(order, ledger, now.AddSeconds(2 * 180 + 120));

        // Assert
        Assert.True(result.Allowed);
    }

    #endregion

    #region Bracket Tests

    [Fact]
    public void Validate_Bracket_RequiresStopLossInLiveMode_Rejects()
    {
        // Arrange
        var rm = CreateRiskManager(new ExecutionOptions { Live = true });
        var bracket = new BracketIntent
        {
            Entry = new OrderIntent
            {
                Symbol = "AAPL",
                Side = OrderSide.Buy,
                Type = OrderType.Market,
                Quantity = 100m
            },
            TakeProfit = new OrderIntent
            {
                Symbol = "AAPL",
                Side = OrderSide.Sell,
                Type = OrderType.Limit,
                Quantity = 100m
            },
            StopLoss = null
        };

        // Act
        var result = rm.Validate(bracket);

        // Assert
        Assert.False(result.Allowed);
        Assert.Contains("StopLoss is required in Live mode", result.Reason);
        Assert.Contains("RequiredStopLossLive", result.Tags);
    }

    [Fact]
    public void Validate_Bracket_AllowsWithoutStopLossWhenLiveDisabled_Allows()
    {
        // Arrange
        var rm = CreateRiskManager(new ExecutionOptions { Live = false });
        var bracket = new BracketIntent
        {
            Entry = new OrderIntent
            {
                Symbol = "AAPL",
                Side = OrderSide.Buy,
                Type = OrderType.Market,
                Quantity = 100m
            },
            TakeProfit = new OrderIntent
            {
                Symbol = "AAPL",
                Side = OrderSide.Sell,
                Type = OrderType.Limit,
                Quantity = 100m
            },
            StopLoss = null
        };

        // Act
        var result = rm.Validate(bracket);

        // Assert
        Assert.True(result.Allowed);
    }

    [Fact]
    public void Validate_Bracket_DailyLimit_Reached_Rejects()
    {
        // Arrange
        var options = new ExecutionOptions { MaxBracketsPerDay = 2 };
        var rm = CreateRiskManager(options);
        var ledger = CreateLedger();
        var now = DateTimeOffset.UtcNow;

        // Add 2 brackets (different symbols to avoid cooldown)
        for (int i = 0; i < 2; i++)
        {
            var symbol = $"SYM{i}";
            var bracket = CreateBracket(symbol, created: now.AddSeconds(i * 200));
            ledger.RecordBracket(bracket);
            ledger.RecordResult(bracket.Entry.IntentId, new ExecutionResult
            {
                IntentId = bracket.Entry.IntentId,
                Status = ExecutionStatus.Submitted,
                TimestampUtc = bracket.Entry.CreatedUtc
            });
        }

        // Try to add 3rd bracket (different symbol, after min time)
        var bracket3 = CreateBracket("SYM2", created: now.AddSeconds(2 * 200 + 30));

        // Act
        var result = rm.Validate(bracket3, ledger, now.AddSeconds(2 * 200 + 30));

        // Assert
        Assert.False(result.Allowed);
        Assert.Contains("Daily bracket limit", result.Reason);
        Assert.Contains("DailyBracketLimit", result.Tags);
    }

    #endregion

    #region Daily Loss Limit Tests

    [Fact]
    public void Validate_DailyLossLimitReached_Rejects()
    {
        var tracker = new FakeOrderStateTracker { RealizedPnlToday = -200m };
        var options = new ExecutionOptions { MaxLossPerDayUsd = 200m };
        var rm = new RiskManagerV0(options, tracker);

        var intent = CreateOrder();
        var result = rm.Validate(intent);

        Assert.False(result.Allowed);
        Assert.Contains("DailyLossLimit", result.Tags);
    }

    [Fact]
    public void Validate_OrderWouldBreachLimit_RejectsPreventively()
    {
        var tracker = new FakeOrderStateTracker { RealizedPnlToday = -150m };
        var options = new ExecutionOptions { MaxLossPerDayUsd = 200m };
        var rm = new RiskManagerV0(options, tracker);

        var intent = new OrderIntent
        {
            Symbol = "AAPL",
            Side = OrderSide.Buy,
            Type = OrderType.Limit,
            Quantity = 10m,
            LimitPrice = 100m,
            StopPrice = 90m,
            CreatedUtc = DateTimeOffset.UtcNow
        };

        var result = rm.Validate(intent);

        Assert.False(result.Allowed);
        Assert.Contains("DailyLossLimitPreventive", result.Tags);
    }

    #endregion

    #region Integration Tests

    [Fact]
    public void Validate_Order_MultipleChecks_CooldownTakesPriority_Rejects()
    {
        // Arrange
        var rm = CreateRiskManager();
        var ledger = CreateLedger();
        var now = DateTimeOffset.UtcNow;

        // Add order on AAPL
        var order1 = CreateOrder("AAPL", created: now);
        ledger.RecordIntent(order1);

        // Try same symbol immediately (violates cooldown AND min time)
        var order2 = CreateOrder("AAPL", created: now.AddSeconds(5));

        // Act
        var result = rm.Validate(order2, ledger, now.AddSeconds(5));

        // Assert
        Assert.False(result.Allowed);
        // Should report cooldown as the issue
        Assert.Contains("cooldown", result.Reason!.ToLower());
    }

    [Fact]
    public void Validate_Order_NoLedger_SkipsLedgerChecks_Allows()
    {
        // Arrange
        var rm = CreateRiskManager();
        var order = CreateOrder();

        // Act (no ledger provided)
        var result = rm.Validate(order, ledger: null);

        // Assert (only basic validation happens)
        Assert.True(result.Allowed);
    }

    #endregion
}

/// <summary>
/// Fake implementation of IExecutionLedger for testing.
/// </summary>
public class FakeExecutionLedger : IExecutionLedger
{
    private readonly List<OrderIntent> _intents = new();
    private readonly List<BracketIntent> _brackets = new();
    private readonly List<ExecutionResult> _results = new();
    private readonly Dictionary<Guid, BracketState> _bracketStates = new();

    public void RecordIntent(OrderIntent intent)
    {
        _intents.Add(intent);
    }

    public void RecordBracket(BracketIntent intent)
    {
        _brackets.Add(intent);
    }

    public void RecordResult(Guid intentId, ExecutionResult result)
    {
        _results.Add(result);
    }

    public IReadOnlyList<OrderIntent> GetIntents() => _intents.AsReadOnly();

    public IReadOnlyList<BracketIntent> GetBrackets() => _brackets.AsReadOnly();

    public IReadOnlyList<ExecutionResult> GetResults() => _results.AsReadOnly();

    public int GetSubmittedIntentCountToday(DateTimeOffset now)
    {
        var todayStart = new DateTimeOffset(now.UtcDateTime.Date, TimeSpan.Zero);
        var todayEnd = todayStart.AddDays(1);

        return _results
            .Where(r => r.Status == ExecutionStatus.Submitted
                        && r.TimestampUtc >= todayStart
                        && r.TimestampUtc < todayEnd)
            .Select(r => r.IntentId)
            .ToHashSet()
            .Count;
    }

    public int GetSubmittedBracketCountToday(DateTimeOffset now)
    {
        var todayStart = new DateTimeOffset(now.UtcDateTime.Date, TimeSpan.Zero);
        var todayEnd = todayStart.AddDays(1);

        var submitted = _results
            .Where(r => r.Status == ExecutionStatus.Submitted
                        && r.TimestampUtc >= todayStart
                        && r.TimestampUtc < todayEnd)
            .Select(r => r.IntentId)
            .ToHashSet();

        return _brackets.Count(b => submitted.Contains(b.Entry.IntentId)
                                    && b.Entry.CreatedUtc >= todayStart
                                    && b.Entry.CreatedUtc < todayEnd);
    }

    public int GetOpenBracketCount()
    {
        return _bracketStates.Values.Count(state => state == BracketState.Open);
    }

    public void UpdateBracketState(Guid entryIntentId, BracketState newState)
    {
        _bracketStates[entryIntentId] = newState;
    }
}

public class FakeOrderStateTracker : IOrderStateTracker
{
    public decimal RealizedPnlToday { get; set; }

    public void TrackSubmittedOrder(int orderId, Guid intentId, string symbol, decimal quantity, OrderSide side)
    {
    }

    public void ProcessOrderStatus(OrderStatusUpdate update)
    {
    }

    public void ProcessFill(FillReport fill)
    {
    }

    public void ProcessCommissionReport(string execId, decimal? commission, decimal? realizedPnl)
    {
    }

    public BrokerOrderStatus GetOrderStatus(int orderId) => BrokerOrderStatus.Unknown;

    public IReadOnlyList<FillReport> GetFillsForOrder(int orderId) => Array.Empty<FillReport>();

    public IReadOnlyList<FillReport> GetFillsForIntent(Guid intentId) => Array.Empty<FillReport>();

    public decimal GetRealizedPnlToday() => RealizedPnlToday;

    public int GetOpenBracketCount() => 0;
}
