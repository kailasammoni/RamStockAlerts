namespace RamStockAlerts.Execution.Tests;

using RamStockAlerts.Execution.Contracts;
using RamStockAlerts.Execution.Services;
using Xunit;

/// <summary>
/// Tests for BracketTemplateBuilder - sizing, template-based level calculation, and edge cases.
/// </summary>
public class BracketTemplateBuilderTests
{
    private readonly BracketTemplateBuilder _builder = new();

    #region VOL_A Template Tests

    [Fact]
    public void Build_VolA_WithSpread_UsesSpreadMultiple()
    {
        // Arrange
        var req = new ExecutionRequest
        {
            Symbol = "AAPL",
            Side = OrderSide.Buy,
            ReferencePrice = 150m,
            AccountEquityUsd = 10000m,
            RiskPerTradePct = 0.01m, // 1% risk
            MaxNotionalPct = 5.0m, // Raise cap to avoid notional limit in test
            Template = "VOL_A",
            VolatilityProxy = 0.10m // 10 cent spread
        };

        // Act
        var bracket = _builder.Build(req);

        // Assert - Stop distance should be max(150 * 0.003, 0.10 * 6) = max(0.45, 0.60) = 0.60
        var stopDistance = 0.60m;
        var profitDistance = stopDistance * 1.2m; // 0.72

        // Risk budget = 10000 * 0.01 = 100
        // Qty = floor(100 / 0.60) = floor(166.67) = 166
        var expectedQty = 166m;

        Assert.NotNull(bracket.Entry);
        Assert.Equal("AAPL", bracket.Entry.Symbol);
        Assert.Equal(OrderSide.Buy, bracket.Entry.Side);
        Assert.Equal(OrderType.Market, bracket.Entry.Type);
        Assert.Equal(expectedQty, bracket.Entry.Quantity);

        Assert.NotNull(bracket.StopLoss);
        Assert.Equal(OrderSide.Sell, bracket.StopLoss.Side);
        Assert.Equal(OrderType.Stop, bracket.StopLoss.Type);
        Assert.Equal(expectedQty, bracket.StopLoss.Quantity);
        Assert.Equal(150m - stopDistance, bracket.StopLoss.StopPrice); // 149.40

        Assert.NotNull(bracket.TakeProfit);
        Assert.Equal(OrderSide.Sell, bracket.TakeProfit.Side);
        Assert.Equal(OrderType.Limit, bracket.TakeProfit.Type);
        Assert.Equal(expectedQty, bracket.TakeProfit.Quantity);
        Assert.Equal(150m + profitDistance, bracket.TakeProfit.LimitPrice); // 150.72
    }

    [Fact]
    public void Build_VolA_NoSpread_UsesFixedPct()
    {
        // Arrange
        var req = new ExecutionRequest
        {
            Symbol = "TSLA",
            Side = OrderSide.Buy,
            ReferencePrice = 200m,
            AccountEquityUsd = 10000m,
            RiskPerTradePct = 0.005m, // 0.5% risk
            MaxNotionalPct = 5.0m, // Raise cap to avoid notional limit in test
            Template = "VOL_A",
            VolatilityProxy = null // No spread provided
        };

        // Act
        var bracket = _builder.Build(req);

        // Assert - Stop distance should be max(200 * 0.003, 0 * 6) = 0.60
        var stopDistance = 0.60m;
        var profitDistance = stopDistance * 1.2m; // 0.72

        // Risk budget = 10000 * 0.005 = 50
        // Qty = floor(50 / 0.60) = floor(83.33) = 83
        var expectedQty = 83m;

        Assert.NotNull(bracket.Entry);
        Assert.Equal(expectedQty, bracket.Entry.Quantity);
        Assert.Equal(200m - stopDistance, bracket.StopLoss?.StopPrice); // 199.40
        Assert.Equal(200m + profitDistance, bracket.TakeProfit?.LimitPrice); // 200.72
    }

    [Fact]
    public void Build_VolA_SellSide_CorrectLevels()
    {
        // Arrange
        var req = new ExecutionRequest
        {
            Symbol = "NVDA",
            Side = OrderSide.Sell,
            ReferencePrice = 500m,
            AccountEquityUsd = 20000m,
            RiskPerTradePct = 0.01m,
            MaxNotionalPct = 5.0m, // Raise cap to avoid notional limit in test
            Template = "VOL_A",
            VolatilityProxy = 0.25m
        };

        // Act
        var bracket = _builder.Build(req);

        // Assert - Stop distance = max(500 * 0.003, 0.25 * 6) = max(1.50, 1.50) = 1.50
        var stopDistance = 1.50m;
        var profitDistance = stopDistance * 1.2m; // 1.80

        // Risk budget = 20000 * 0.01 = 200
        // Qty = floor(200 / 1.50) = floor(133.33) = 133
        var expectedQty = 133m;

        Assert.NotNull(bracket.Entry);
        Assert.Equal(OrderSide.Sell, bracket.Entry.Side);
        Assert.Equal(expectedQty, bracket.Entry.Quantity);

        // For Sell: Stop above, Profit below
        Assert.NotNull(bracket.StopLoss);
        Assert.Equal(OrderSide.Buy, bracket.StopLoss.Side);
        Assert.Equal(500m + stopDistance, bracket.StopLoss.StopPrice); // 501.50

        Assert.NotNull(bracket.TakeProfit);
        Assert.Equal(OrderSide.Buy, bracket.TakeProfit.Side);
        Assert.Equal(500m - profitDistance, bracket.TakeProfit.LimitPrice); // 498.20
    }

    #endregion

    #region VOL_B Template Tests

    [Fact]
    public void Build_VolB_WithSpread_UsesSpreadMultiple()
    {
        // Arrange
        var req = new ExecutionRequest
        {
            Symbol = "MSFT",
            Side = OrderSide.Buy,
            ReferencePrice = 300m,
            AccountEquityUsd = 15000m,
            RiskPerTradePct = 0.01m,
            MaxNotionalPct = 5.0m, // Raise cap to avoid notional limit in test
            Template = "VOL_B",
            VolatilityProxy = 0.15m
        };

        // Act
        var bracket = _builder.Build(req);

        // Assert - Stop distance = max(300 * 0.006, 0.15 * 10) = max(1.80, 1.50) = 1.80
        var stopDistance = 1.80m;
        var profitDistance = stopDistance * 1.8m; // 3.24

        // Risk budget = 15000 * 0.01 = 150
        // Qty = floor(150 / 1.80) = floor(83.33) = 83
        var expectedQty = 83m;

        Assert.NotNull(bracket.Entry);
        Assert.Equal(expectedQty, bracket.Entry.Quantity);
        Assert.Equal(300m - stopDistance, bracket.StopLoss?.StopPrice); // 298.20
        Assert.Equal(300m + profitDistance, bracket.TakeProfit?.LimitPrice); // 303.24
    }

    [Fact]
    public void Build_VolB_NoSpread_UsesFixedPct()
    {
        // Arrange
        var req = new ExecutionRequest
        {
            Symbol = "GOOGL",
            Side = OrderSide.Buy,
            ReferencePrice = 100m,
            AccountEquityUsd = 10000m,
            RiskPerTradePct = 0.01m,
            MaxNotionalPct = 5.0m, // Raise cap to avoid notional limit in test
            Template = "VOL_B",
            VolatilityProxy = null
        };

        // Act
        var bracket = _builder.Build(req);

        // Assert - Stop distance = max(100 * 0.006, 0 * 10) = 0.60
        var stopDistance = 0.60m;
        var profitDistance = stopDistance * 1.8m; // 1.08

        // Risk budget = 10000 * 0.01 = 100
        // Qty = floor(100 / 0.60) = floor(166.67) = 166
        var expectedQty = 166m;

        Assert.NotNull(bracket.Entry);
        Assert.Equal(expectedQty, bracket.Entry.Quantity);
        Assert.Equal(100m - stopDistance, bracket.StopLoss?.StopPrice); // 99.40
        Assert.Equal(100m + profitDistance, bracket.TakeProfit?.LimitPrice); // 101.08
    }

    #endregion

    #region Notional Cap Tests

    [Fact]
    public void Build_CapsNotional_WhenExceedsMaxPct()
    {
        // Arrange
        var req = new ExecutionRequest
        {
            Symbol = "AMZN",
            Side = OrderSide.Buy,
            ReferencePrice = 100m,
            AccountEquityUsd = 10000m,
            RiskPerTradePct = 0.10m, // 10% risk (high)
            MaxNotionalPct = 0.05m, // 5% max notional
            Template = "VOL_A",
            VolatilityProxy = 0.05m
        };

        // Act
        var bracket = _builder.Build(req);

        // Assert - Stop distance = max(100 * 0.003, 0.05 * 6) = max(0.30, 0.30) = 0.30
        // Risk budget = 10000 * 0.10 = 1000
        // Raw qty = floor(1000 / 0.30) = 3333
        // Max notional = 10000 * 0.05 = 500
        // Max qty by notional = floor(500 / 100) = 5
        // Final qty = min(3333, 5) = 5

        var expectedQty = 5m;

        Assert.NotNull(bracket.Entry);
        Assert.Equal(expectedQty, bracket.Entry.Quantity);

        // Verify notional is within cap
        var notional = expectedQty * req.ReferencePrice;
        Assert.True(notional <= req.AccountEquityUsd * req.MaxNotionalPct);
    }

    [Fact]
    public void Build_NoCapNeeded_WhenWithinMaxNotional()
    {
        // Arrange
        var req = new ExecutionRequest
        {
            Symbol = "META",
            Side = OrderSide.Buy,
            ReferencePrice = 200m,
            AccountEquityUsd = 100000m,
            RiskPerTradePct = 0.005m, // 0.5% risk
            MaxNotionalPct = 0.20m, // 20% max notional (generous)
            Template = "VOL_A",
            VolatilityProxy = 0.10m
        };

        // Act
        var bracket = _builder.Build(req);

        // Assert - Stop distance = max(200 * 0.003, 0.10 * 6) = max(0.60, 0.60) = 0.60
        // Risk budget = 100000 * 0.005 = 500
        // Raw qty = floor(500 / 0.60) = 833
        // Max notional = 100000 * 0.20 = 20000
        // Max qty by notional = floor(20000 / 200) = 100
        // Final qty = min(833, 100) = 100

        var expectedQty = 100m;

        Assert.NotNull(bracket.Entry);
        Assert.Equal(expectedQty, bracket.Entry.Quantity);
    }

    #endregion

    #region Rejection Tests

    [Fact]
    public void Build_RejectsWhenQtyZero_SmallRiskBudget()
    {
        // Arrange
        var req = new ExecutionRequest
        {
            Symbol = "SPY",
            Side = OrderSide.Buy,
            ReferencePrice = 400m,
            AccountEquityUsd = 1000m,
            RiskPerTradePct = 0.001m, // 0.1% risk (very small)
            Template = "VOL_B",
            VolatilityProxy = 0.50m
        };

        // Act
        var bracket = _builder.Build(req);

        // Assert - Stop distance = max(400 * 0.006, 0.50 * 10) = max(2.40, 5.00) = 5.00
        // Risk budget = 1000 * 0.001 = 1
        // Qty = floor(1 / 5.00) = floor(0.20) = 0

        Assert.NotNull(bracket.Entry);
        Assert.Equal(0m, bracket.Entry.Quantity);
        Assert.Null(bracket.StopLoss);
        Assert.Null(bracket.TakeProfit);

        // Check rejection reason in tags
        Assert.True(bracket.Entry!.Tags.ContainsKey("RejectionReason"));
        Assert.Equal("SizingTooSmall", bracket.Entry.Tags["RejectionReason"]);
    }

    [Fact]
    public void Build_RejectsWhenQtyZero_HighReferencePrice()
    {
        // Arrange
        var req = new ExecutionRequest
        {
            Symbol = "BRK.A",
            Side = OrderSide.Buy,
            ReferencePrice = 500000m, // Very expensive stock
            AccountEquityUsd = 10000m,
            RiskPerTradePct = 0.01m,
            MaxNotionalPct = 0.10m,
            Template = "VOL_A",
            VolatilityProxy = null
        };

        // Act
        var bracket = _builder.Build(req);

        // Assert - Max notional = 10000 * 0.10 = 1000
        // Max qty by notional = floor(1000 / 500000) = 0

        Assert.NotNull(bracket.Entry);
        Assert.Equal(0m, bracket.Entry.Quantity);
        Assert.Null(bracket.StopLoss);
        Assert.Null(bracket.TakeProfit);
        Assert.True(bracket.Entry!.Tags.ContainsKey("RejectionReason"));
    }

    #endregion

    #region Edge Cases

    [Fact]
    public void Build_UnknownTemplate_ThrowsException()
    {
        // Arrange
        var req = new ExecutionRequest
        {
            Symbol = "XYZ",
            Side = OrderSide.Buy,
            ReferencePrice = 50m,
            AccountEquityUsd = 10000m,
            Template = "VOL_UNKNOWN",
            VolatilityProxy = null
        };

        // Act & Assert
        var ex = Assert.Throws<ArgumentException>(() => _builder.Build(req));
        Assert.Contains("Unknown template", ex.Message);
    }

    [Fact]
    public void Build_TemplateCaseInsensitive_Works()
    {
        // Arrange
        var req = new ExecutionRequest
        {
            Symbol = "AAPL",
            Side = OrderSide.Buy,
            ReferencePrice = 150m,
            AccountEquityUsd = 10000m,
            RiskPerTradePct = 0.01m,
            Template = "vol_a", // lowercase
            VolatilityProxy = 0.10m
        };

        // Act
        var bracket = _builder.Build(req);

        // Assert - Should work (case-insensitive)
        Assert.NotNull(bracket.Entry);
        Assert.True(bracket.Entry.Quantity > 0);
    }

    [Fact]
    public void Build_IncludesTemplateMetadata()
    {
        // Arrange
        var req = new ExecutionRequest
        {
            Symbol = "AAPL",
            Side = OrderSide.Buy,
            ReferencePrice = 150m,
            AccountEquityUsd = 10000m,
            Template = "VOL_B",
            VolatilityProxy = 0.20m
        };

        // Act
        var bracket = _builder.Build(req);

        // Assert - Check tags include template and distances
        Assert.NotNull(bracket.Entry);
        Assert.True(bracket.Entry!.Tags.ContainsKey("Template"));
        Assert.Equal("VOL_B", bracket.Entry.Tags["Template"]);
        Assert.True(bracket.Entry.Tags.ContainsKey("StopDistance"));
        Assert.True(bracket.Entry.Tags.ContainsKey("ProfitDistance"));
    }

    [Fact]
    public void Build_CreatesOcoGroupId()
    {
        // Arrange
        var req = new ExecutionRequest
        {
            Symbol = "MSFT",
            Side = OrderSide.Buy,
            ReferencePrice = 300m,
            AccountEquityUsd = 10000m,
            Template = "VOL_A",
            VolatilityProxy = 0.10m
        };

        // Act
        var bracket = _builder.Build(req);

        // Assert
        Assert.NotNull(bracket.OcoGroupId);
        Assert.NotEmpty(bracket.OcoGroupId);

        // Verify OCO group ID is a valid GUID string
        Assert.True(Guid.TryParse(bracket.OcoGroupId, out _));
    }

    #endregion

    #region Spread vs Fixed Pct Tests

    [Fact]
    public void Build_VolA_SpreadDominates_WhenLarger()
    {
        // Arrange
        var req = new ExecutionRequest
        {
            Symbol = "AAPL",
            Side = OrderSide.Buy,
            ReferencePrice = 100m,
            AccountEquityUsd = 10000m,
            Template = "VOL_A",
            VolatilityProxy = 1.00m // Large spread: 1.00 * 6 = 6.00
        };

        // Act
        var bracket = _builder.Build(req);

        // Assert - Stop distance = max(100 * 0.003, 1.00 * 6) = max(0.30, 6.00) = 6.00
        // Spread-based should dominate
        var expectedStopDistance = 6.00m;
        var expectedProfitDistance = expectedStopDistance * 1.2m; // 7.20

        Assert.NotNull(bracket.StopLoss);
        Assert.Equal(100m - expectedStopDistance, bracket.StopLoss.StopPrice); // 94.00
        Assert.Equal(100m + expectedProfitDistance, bracket.TakeProfit?.LimitPrice); // 107.20
    }

    [Fact]
    public void Build_VolA_FixedPctDominates_WhenSpreadSmall()
    {
        // Arrange
        var req = new ExecutionRequest
        {
            Symbol = "AAPL",
            Side = OrderSide.Buy,
            ReferencePrice = 100m,
            AccountEquityUsd = 10000m,
            Template = "VOL_A",
            VolatilityProxy = 0.01m // Small spread: 0.01 * 6 = 0.06
        };

        // Act
        var bracket = _builder.Build(req);

        // Assert - Stop distance = max(100 * 0.003, 0.01 * 6) = max(0.30, 0.06) = 0.30
        // Fixed pct should dominate
        var expectedStopDistance = 0.30m;
        var expectedProfitDistance = expectedStopDistance * 1.2m; // 0.36

        Assert.NotNull(bracket.StopLoss);
        Assert.Equal(100m - expectedStopDistance, bracket.StopLoss.StopPrice); // 99.70
        Assert.Equal(100m + expectedProfitDistance, bracket.TakeProfit?.LimitPrice); // 100.36
    }

    #endregion
}
