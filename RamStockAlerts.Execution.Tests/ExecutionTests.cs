namespace RamStockAlerts.Execution.Tests;

using RamStockAlerts.Execution.Contracts;
using RamStockAlerts.Execution.Interfaces;
using RamStockAlerts.Execution.Risk;
using RamStockAlerts.Execution.Services;
using RamStockAlerts.Execution.Storage;
using Xunit;
// Use test version of FakeBrokerClient (has extra methods for failure injection)
using FakeBrokerClient = RamStockAlerts.Execution.Tests.Fakes.FakeBrokerClient;


/// <summary>
/// Unit tests for the Execution module.
/// </summary>
public class ExecutionTests
{
    /// <summary>
    /// Helper to create default ExecutionOptions for tests.
    /// </summary>
    private static ExecutionOptions CreateDefaultOptions() => new()
    {
        Enabled = true,
        KillSwitch = false,
        MaxOrdersPerDay = 100,
        MaxBracketsPerDay = 100,
        MaxOpenPositions = 10
    };

    #region RiskManager Tests

    [Fact]
    public void RiskManagerV0_Reject_MissingSymbol()
    {
        // Arrange
        var riskManager = new RiskManagerV0(CreateDefaultOptions());
        var intent = new OrderIntent
        {
            Symbol = null, // Missing symbol
            Side = OrderSide.Buy,
            Type = OrderType.Market,
            Quantity = 100m
        };

        // Act
        var decision = riskManager.Validate(intent);

        // Assert
        Assert.False(decision.Allowed);
        Assert.NotNull(decision.Reason);
        Assert.Contains("Symbol", decision.Reason);
    }

    [Fact]
    public void RiskManagerV0_Reject_BothQuantityAndNotionalNull()
    {
        // Arrange
        var riskManager = new RiskManagerV0(CreateDefaultOptions());
        var intent = new OrderIntent
        {
            Symbol = "AAPL",
            Side = OrderSide.Buy,
            Type = OrderType.Market,
            Quantity = null, // Both null
            NotionalUsd = null
        };

        // Act
        var decision = riskManager.Validate(intent);

        // Assert
        Assert.False(decision.Allowed);
        Assert.NotNull(decision.Reason);
        Assert.Contains("Quantity", decision.Reason);
    }

    [Fact]
    public void RiskManagerV0_Accept_ValidOrderWithQuantity()
    {
        // Arrange
        var riskManager = new RiskManagerV0(CreateDefaultOptions());
        var intent = new OrderIntent
        {
            Symbol = "AAPL",
            Side = OrderSide.Buy,
            Type = OrderType.Market,
            Quantity = 100m
        };

        // Act
        var decision = riskManager.Validate(intent);

        // Assert
        Assert.True(decision.Allowed);
        Assert.Null(decision.Reason);
    }

    [Fact]
    public void RiskManagerV0_Accept_ValidOrderWithNotional()
    {
        // Arrange
        var riskManager = new RiskManagerV0(CreateDefaultOptions());
        var intent = new OrderIntent
        {
            Symbol = "AAPL",
            Side = OrderSide.Buy,
            Type = OrderType.Market,
            NotionalUsd = 1500m
        };

        // Act
        var decision = riskManager.Validate(intent);

        // Assert
        Assert.True(decision.Allowed);
    }

    [Fact]
    public void RiskManagerV0_Reject_ExceedsMaxNotional()
    {
        // Arrange
        var riskManager = new RiskManagerV0(CreateDefaultOptions(), maxNotionalUsd: 2000m);
        var intent = new OrderIntent
        {
            Symbol = "AAPL",
            Side = OrderSide.Buy,
            Type = OrderType.Market,
            NotionalUsd = 2500m // Exceeds default 2000
        };

        // Act
        var decision = riskManager.Validate(intent);

        // Assert
        Assert.False(decision.Allowed);
        Assert.NotNull(decision.Reason);
        Assert.Contains("exceeds", decision.Reason.ToLower());
    }

    [Fact]
    public void RiskManagerV0_Reject_ExceedsMaxShares()
    {
        // Arrange
        var riskManager = new RiskManagerV0(CreateDefaultOptions(), maxShares: 500m);
        var intent = new OrderIntent
        {
            Symbol = "AAPL",
            Side = OrderSide.Buy,
            Type = OrderType.Market,
            Quantity = 600m // Exceeds default 500
        };

        // Act
        var decision = riskManager.Validate(intent);

        // Assert
        Assert.False(decision.Allowed);
        Assert.NotNull(decision.Reason);
        Assert.Contains("exceeds", decision.Reason.ToLower());
    }

    [Fact]
    public void RiskManagerV0_Reject_LiveBracketWithoutStopLoss()
    {
        // Arrange
        var riskManager = new RiskManagerV0(CreateDefaultOptions());
        var bracket = new BracketIntent
        {
            Entry = new OrderIntent
            {
                Symbol = "AAPL",
                Mode = TradingMode.Live, // Live mode
                Side = OrderSide.Buy,
                Type = OrderType.Limit,
                Quantity = 100m,
                LimitPrice = 150m
            },
            StopLoss = null, // Missing stop-loss in live mode
            TakeProfit = new OrderIntent
            {
                Symbol = "AAPL",
                Mode = TradingMode.Live,
                Side = OrderSide.Sell,
                Type = OrderType.Limit,
                Quantity = 100m,
                LimitPrice = 160m
            }
        };

        // Act
        var decision = riskManager.Validate(bracket);

        // Assert
        Assert.False(decision.Allowed);
        Assert.NotNull(decision.Reason);
        Assert.Contains("StopLoss", decision.Reason);
    }

    [Fact]
    public void RiskManagerV0_Accept_LiveBracketWithStopLoss()
    {
        // Arrange
        var riskManager = new RiskManagerV0(CreateDefaultOptions());
        var bracket = new BracketIntent
        {
            Entry = new OrderIntent
            {
                Symbol = "AAPL",
                Mode = TradingMode.Live,
                Side = OrderSide.Buy,
                Type = OrderType.Limit,
                Quantity = 100m,
                LimitPrice = 150m
            },
            StopLoss = new OrderIntent
            {
                Symbol = "AAPL",
                Mode = TradingMode.Live,
                Side = OrderSide.Sell,
                Type = OrderType.Stop,
                Quantity = 100m,
                StopPrice = 140m
            },
            TakeProfit = new OrderIntent
            {
                Symbol = "AAPL",
                Mode = TradingMode.Live,
                Side = OrderSide.Sell,
                Type = OrderType.Limit,
                Quantity = 100m,
                LimitPrice = 160m
            }
        };

        // Act
        var decision = riskManager.Validate(bracket);

        // Assert
        Assert.True(decision.Allowed);
    }

    [Fact]
    public void RiskManagerV0_Accept_PaperBracketWithoutStopLoss()
    {
        // Arrange
        var riskManager = new RiskManagerV0(CreateDefaultOptions());
        var bracket = new BracketIntent
        {
            Entry = new OrderIntent
            {
                Symbol = "AAPL",
                Mode = TradingMode.Paper, // Paper mode, so stop-loss not required
                Side = OrderSide.Buy,
                Type = OrderType.Limit,
                Quantity = 100m,
                LimitPrice = 150m
            },
            StopLoss = null, // No stop-loss
            TakeProfit = new OrderIntent
            {
                Symbol = "AAPL",
                Mode = TradingMode.Paper,
                Side = OrderSide.Sell,
                Type = OrderType.Limit,
                Quantity = 100m,
                LimitPrice = 160m
            }
        };

        // Act
        var decision = riskManager.Validate(bracket);

        // Assert
        Assert.True(decision.Allowed);
    }

    #endregion

    #region ExecutionService Tests

    [Fact]
    public async Task ExecutionService_ExecuteAsync_Order_Success_RecordsInLedger()
    {
        // Arrange
        var riskManager = new RiskManagerV0(CreateDefaultOptions());
        var brokerClient = new FakeBrokerClient();
        var ledger = new InMemoryExecutionLedger();
        var service = new ExecutionService(riskManager, brokerClient, ledger);

        var intent = new OrderIntent
        {
            IntentId = Guid.NewGuid(),
            Symbol = "AAPL",
            Mode = TradingMode.Paper,
            Side = OrderSide.Buy,
            Type = OrderType.Market,
            Quantity = 100m
        };

        // Act
        var result = await service.ExecuteAsync(intent);

        // Assert
        Assert.Equal(ExecutionStatus.Accepted, result.Status);
        Assert.NotNull(result.BrokerOrderIds);
        Assert.Single(result.BrokerOrderIds);

        // Verify ledger recorded intent and result
        var intents = ledger.GetIntents();
        Assert.Single(intents);
        Assert.Equal(intent.IntentId, intents[0].IntentId);

        var results = ledger.GetResults();
        Assert.Single(results);
        Assert.Equal(ExecutionStatus.Accepted, results[0].Status);
    }

    [Fact]
    public async Task ExecutionService_ExecuteAsync_Order_Rejected_DoesNotCallBroker()
    {
        // Arrange
        var riskManager = new RiskManagerV0(CreateDefaultOptions());
        var brokerClient = new FakeBrokerClient();
        var ledger = new InMemoryExecutionLedger();
        var service = new ExecutionService(riskManager, brokerClient, ledger);

        var intent = new OrderIntent
        {
            IntentId = Guid.NewGuid(),
            Symbol = "AAPL",
            Mode = TradingMode.Paper,
            Side = OrderSide.Buy,
            Type = OrderType.Market,
            Quantity = null,
            NotionalUsd = null // Both null, will be rejected by risk manager
        };

        // Act
        var result = await service.ExecuteAsync(intent);

        // Assert
        Assert.Equal(ExecutionStatus.Rejected, result.Status);
        Assert.NotNull(result.RejectionReason);

        // Verify broker was NOT called
        Assert.Empty(brokerClient.GetPlacedOrders());

        // But ledger should still record the rejection
        var results = ledger.GetResults();
        Assert.Single(results);
        Assert.Equal(ExecutionStatus.Rejected, results[0].Status);
    }

    [Fact]
    public async Task ExecutionService_ExecuteAsync_Bracket_Success_RecordsInLedger()
    {
        // Arrange
        var riskManager = new RiskManagerV0(CreateDefaultOptions());
        var brokerClient = new FakeBrokerClient();
        var ledger = new InMemoryExecutionLedger();
        var service = new ExecutionService(riskManager, brokerClient, ledger);

        var bracket = new BracketIntent
        {
            Entry = new OrderIntent
            {
                IntentId = Guid.NewGuid(),
                Symbol = "AAPL",
                Mode = TradingMode.Paper,
                Side = OrderSide.Buy,
                Type = OrderType.Limit,
                Quantity = 100m,
                LimitPrice = 150m
            },
            StopLoss = new OrderIntent
            {
                Symbol = "AAPL",
                Mode = TradingMode.Paper,
                Side = OrderSide.Sell,
                Type = OrderType.Stop,
                Quantity = 100m,
                StopPrice = 140m
            },
            TakeProfit = new OrderIntent
            {
                Symbol = "AAPL",
                Mode = TradingMode.Paper,
                Side = OrderSide.Sell,
                Type = OrderType.Limit,
                Quantity = 100m,
                LimitPrice = 160m
            }
        };

        // Act
        var result = await service.ExecuteAsync(bracket);

        // Assert
        Assert.Equal(ExecutionStatus.Accepted, result.Status);
        Assert.NotNull(result.BrokerOrderIds);
        Assert.Equal(3, result.BrokerOrderIds.Count); // Entry + Stop + Profit

        // Verify ledger recorded bracket and result
        var brackets = ledger.GetBrackets();
        Assert.Single(brackets);

        var results = ledger.GetResults();
        Assert.Single(results);
        Assert.Equal(ExecutionStatus.Accepted, results[0].Status);
    }

    [Fact]
    public async Task ExecutionService_ExecuteAsync_Bracket_Rejected_DoesNotCallBroker()
    {
        // Arrange
        var riskManager = new RiskManagerV0(CreateDefaultOptions());
        var brokerClient = new FakeBrokerClient();
        var ledger = new InMemoryExecutionLedger();
        var service = new ExecutionService(riskManager, brokerClient, ledger);

        var bracket = new BracketIntent
        {
            Entry = new OrderIntent
            {
                Symbol = "AAPL",
                Mode = TradingMode.Live,
                Side = OrderSide.Buy,
                Type = OrderType.Limit,
                Quantity = 100m,
                LimitPrice = 150m
            },
            StopLoss = null, // Missing in Live mode, will be rejected
            TakeProfit = new OrderIntent
            {
                Symbol = "AAPL",
                Mode = TradingMode.Live,
                Side = OrderSide.Sell,
                Type = OrderType.Limit,
                Quantity = 100m,
                LimitPrice = 160m
            }
        };

        // Act
        var result = await service.ExecuteAsync(bracket);

        // Assert
        Assert.Equal(ExecutionStatus.Rejected, result.Status);

        // Verify broker was NOT called
        Assert.Empty(brokerClient.GetPlacedBrackets());

        // But ledger should record the rejection
        var results = ledger.GetResults();
        Assert.Single(results);
        Assert.Equal(ExecutionStatus.Rejected, results[0].Status);
    }

    #endregion

    #region Ledger Tests

    [Fact]
    public void InMemoryExecutionLedger_RecordIntent_And_Retrieve()
    {
        // Arrange
        var ledger = new InMemoryExecutionLedger();
        var intent = new OrderIntent
        {
            Symbol = "AAPL",
            Side = OrderSide.Buy,
            Type = OrderType.Market,
            Quantity = 100m
        };

        // Act
        ledger.RecordIntent(intent);
        var intents = ledger.GetIntents();

        // Assert
        Assert.Single(intents);
        Assert.Equal(intent.IntentId, intents[0].IntentId);
    }

    [Fact]
    public void InMemoryExecutionLedger_RecordBracket_And_Retrieve()
    {
        // Arrange
        var ledger = new InMemoryExecutionLedger();
        var bracket = new BracketIntent
        {
            Entry = new OrderIntent { Symbol = "AAPL", Side = OrderSide.Buy, Quantity = 100m }
        };

        // Act
        ledger.RecordBracket(bracket);
        var brackets = ledger.GetBrackets();

        // Assert
        Assert.Single(brackets);
    }

    [Fact]
    public void InMemoryExecutionLedger_RecordResult_And_Retrieve()
    {
        // Arrange
        var ledger = new InMemoryExecutionLedger();
        var result = new ExecutionResult
        {
            Status = ExecutionStatus.Accepted,
            BrokerName = "TestBroker",
            BrokerOrderIds = new() { "ORD-123" }
        };

        // Act
        ledger.RecordResult(Guid.NewGuid(), result);
        var results = ledger.GetResults();

        // Assert
        Assert.Single(results);
        Assert.Equal(ExecutionStatus.Accepted, results[0].Status);
    }

    #endregion

    #region FakeBrokerClient Tests

    [Fact]
    public async Task FakeBrokerClient_PlaceAsync_Returns_Accepted()
    {
        // Arrange
        var brokerClient = new FakeBrokerClient();
        var intent = new OrderIntent
        {
            Symbol = "AAPL",
            Side = OrderSide.Buy,
            Type = OrderType.Market,
            Quantity = 100m
        };

        // Act
        var result = await brokerClient.PlaceAsync(intent);

        // Assert
        Assert.Equal(ExecutionStatus.Accepted, result.Status);
        Assert.NotNull(result.BrokerOrderIds);
        Assert.Single(result.BrokerOrderIds);
        Assert.Equal("FakeBroker", result.BrokerName);
    }

    [Fact]
    public async Task FakeBrokerClient_PlaceBracketAsync_Returns_3OrderIds()
    {
        // Arrange
        var brokerClient = new FakeBrokerClient();
        var bracket = new BracketIntent
        {
            Entry = new OrderIntent { Symbol = "AAPL", Side = OrderSide.Buy, Quantity = 100m },
            StopLoss = new OrderIntent { Symbol = "AAPL", Side = OrderSide.Sell, Quantity = 100m }
        };

        // Act
        var result = await brokerClient.PlaceBracketAsync(bracket);

        // Assert
        Assert.Equal(ExecutionStatus.Accepted, result.Status);
        Assert.Equal(3, result.BrokerOrderIds.Count);
    }

    [Fact]
    public async Task FakeBrokerClient_AddFailureSymbol_Returns_Error()
    {
        // Arrange
        var brokerClient = new FakeBrokerClient();
        brokerClient.AddFailureSymbol("FAIL");

        var intent = new OrderIntent
        {
            Symbol = "FAIL",
            Side = OrderSide.Buy,
            Type = OrderType.Market,
            Quantity = 100m
        };

        // Act
        var result = await brokerClient.PlaceAsync(intent);

        // Assert
        Assert.Equal(ExecutionStatus.Error, result.Status);
        Assert.NotNull(result.RejectionReason);
    }

    [Fact]
    public async Task FakeBrokerClient_SetNextFailure_Returns_Rejected()
    {
        // Arrange
        var brokerClient = new FakeBrokerClient();
        brokerClient.SetNextFailure("Broker down");

        var intent = new OrderIntent
        {
            Symbol = "AAPL",
            Side = OrderSide.Buy,
            Type = OrderType.Market,
            Quantity = 100m
        };

        // Act
        var result = await brokerClient.PlaceAsync(intent);

        // Assert
        Assert.Equal(ExecutionStatus.Rejected, result.Status);
        Assert.Equal("Broker down", result.RejectionReason);

        // Verify next call succeeds (failure was single-use)
        var result2 = await brokerClient.PlaceAsync(intent);
        Assert.Equal(ExecutionStatus.Accepted, result2.Status);
    }

    #endregion
}