namespace RamStockAlerts.Tests;

using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using RamStockAlerts.Controllers;
using RamStockAlerts.Controllers.Api.Execution;
using RamStockAlerts.Execution.Contracts;
using RamStockAlerts.Execution.Interfaces;
using RamStockAlerts.Execution.Risk;
using RamStockAlerts.Execution.Services;
using RamStockAlerts.Execution.Storage;
using Xunit;

/// <summary>
/// Tests for ExecutionController REST API endpoints.
/// </summary>
public class ExecutionControllerTests
{
    private readonly ExecutionController _controller;
    private readonly IExecutionLedger _ledger;

    public ExecutionControllerTests()
    {
        var options = new ExecutionOptions { Enabled = true };
        var riskManager = new RiskManagerV0(options);
        var brokerClient = new FakeBrokerClient();
        _ledger = new InMemoryExecutionLedger();
        var executionService = new ExecutionService(riskManager, brokerClient, _ledger);
        var logger = new LoggerFactory().CreateLogger<ExecutionController>();

        // Create mock configuration with Execution:Enabled = true
        var configDict = new Dictionary<string, string?>
        {
            ["Execution:Enabled"] = "true"
        };
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(configDict)
            .Build();

        _controller = new ExecutionController(executionService, _ledger, logger, configuration);
    }

    [Fact]
    public async Task ExecuteOrder_ValidOrder_Returns200WithResult()
    {
        // Arrange
        var dto = new OrderIntentDto
        {
            Mode = "Paper",
            Symbol = "AAPL",
            Side = "Buy",
            Type = "Market",
            Quantity = 100m
        };

        // Act
        var result = await _controller.ExecuteOrder(dto);

        // Assert
        var okResult = Assert.IsType<OkObjectResult>(result);
        var executionResult = Assert.IsType<ExecutionResult>(okResult.Value);
        Assert.Equal(ExecutionStatus.Accepted, executionResult.Status);
        Assert.NotNull(executionResult.BrokerOrderIds);
        Assert.Single(executionResult.BrokerOrderIds);
        Assert.Equal("FakeBroker", executionResult.BrokerName);

        // Verify ledger recorded the intent
        var intents = _ledger.GetIntents();
        Assert.Single(intents);
        Assert.Equal("AAPL", intents[0].Symbol);
    }

    [Fact]
    public async Task ExecuteOrder_InvalidDto_Returns400()
    {
        // Arrange
        var dto = new OrderIntentDto
        {
            Mode = "Paper",
            Symbol = "", // Invalid: empty symbol
            Side = "Buy",
            Type = "Market",
            Quantity = 100m
        };

        // Manually trigger validation
        _controller.ModelState.AddModelError("Symbol", "Symbol is required");

        // Act
        var result = await _controller.ExecuteOrder(dto);

        // Assert
        Assert.IsType<BadRequestObjectResult>(result);
    }

    [Fact]
    public async Task ExecuteOrder_RejectedByRisk_Returns200WithRejectedStatus()
    {
        // Arrange
        var dto = new OrderIntentDto
        {
            Mode = "Paper",
            Symbol = "AAPL",
            Side = "Buy",
            Type = "Market",
            Quantity = 5000m // Exceeds max shares (500)
        };

        // Act
        var result = await _controller.ExecuteOrder(dto);

        // Assert
        var okResult = Assert.IsType<OkObjectResult>(result);
        var executionResult = Assert.IsType<ExecutionResult>(okResult.Value);
        Assert.Equal(ExecutionStatus.Rejected, executionResult.Status);
        Assert.NotNull(executionResult.RejectionReason);
        Assert.Contains("exceeds", executionResult.RejectionReason.ToLower());
    }

    [Fact]
    public async Task ExecuteBracket_ValidBracket_Returns200WithResult()
    {
        // Arrange
        var dto = new BracketIntentDto
        {
            Entry = new OrderIntentDto
            {
                Mode = "Paper",
                Symbol = "TSLA",
                Side = "Buy",
                Type = "Limit",
                Quantity = 50m,
                LimitPrice = 200m
            },
            StopLoss = new OrderIntentDto
            {
                Mode = "Paper",
                Symbol = "TSLA",
                Side = "Sell",
                Type = "Stop",
                Quantity = 50m,
                StopPrice = 190m
            },
            TakeProfit = new OrderIntentDto
            {
                Mode = "Paper",
                Symbol = "TSLA",
                Side = "Sell",
                Type = "Limit",
                Quantity = 50m,
                LimitPrice = 210m
            }
        };

        // Act
        var result = await _controller.ExecuteBracket(dto);

        // Assert
        var okResult = Assert.IsType<OkObjectResult>(result);
        var executionResult = Assert.IsType<ExecutionResult>(okResult.Value);
        Assert.Equal(ExecutionStatus.Accepted, executionResult.Status);
        Assert.NotNull(executionResult.BrokerOrderIds);
        Assert.Equal(3, executionResult.BrokerOrderIds.Count); // Entry + Stop + Profit

        // Verify ledger recorded the bracket
        var brackets = _ledger.GetBrackets();
        Assert.Single(brackets);
        Assert.Equal("TSLA", brackets[0].Entry.Symbol);
    }

    [Fact]
    public async Task ExecuteBracket_LiveModeWithoutStopLoss_Returns200WithRejectedStatus()
    {
        // Arrange
        var dto = new BracketIntentDto
        {
            Entry = new OrderIntentDto
            {
                Mode = "Live", // Live mode requires stop-loss
                Symbol = "GOOG",
                Side = "Buy",
                Type = "Limit",
                Quantity = 25m,
                LimitPrice = 100m
            },
            StopLoss = null, // Missing stop-loss
            TakeProfit = new OrderIntentDto
            {
                Mode = "Live",
                Symbol = "GOOG",
                Side = "Sell",
                Type = "Limit",
                Quantity = 25m,
                LimitPrice = 110m
            }
        };

        // Act
        var result = await _controller.ExecuteBracket(dto);

        // Assert
        var okResult = Assert.IsType<OkObjectResult>(result);
        var executionResult = Assert.IsType<ExecutionResult>(okResult.Value);
        Assert.Equal(ExecutionStatus.Rejected, executionResult.Status);
        Assert.NotNull(executionResult.RejectionReason);
        Assert.Contains("StopLoss", executionResult.RejectionReason);
    }

    [Fact]
    public void GetLedger_AfterExecutions_ReturnsIntentsAndResults()
    {
        // Arrange - execute an order first
        var dto = new OrderIntentDto
        {
            Mode = "Paper",
            Symbol = "MSFT",
            Side = "Sell",
            Type = "Market",
            Quantity = 75m
        };
        _controller.ExecuteOrder(dto).Wait();

        // Act
        var result = _controller.GetLedger();

        // Assert
        var okResult = Assert.IsType<OkObjectResult>(result);
        var ledgerData = Assert.IsType<LedgerDto>(okResult.Value);
        Assert.NotEmpty(ledgerData.Intents);
        Assert.NotEmpty(ledgerData.Results);
        Assert.Single(ledgerData.Intents);
        Assert.Single(ledgerData.Results);
        Assert.Equal("MSFT", ledgerData.Intents[0].Symbol);
    }

    [Fact]
    public void GetLedger_EmptyLedger_ReturnsEmptyLists()
    {
        // Arrange - fresh controller with empty ledger
        var freshLedger = new InMemoryExecutionLedger();
        var options = new ExecutionOptions { Enabled = true };
        var riskManager = new RiskManagerV0(options);
        var brokerClient = new FakeBrokerClient();
        var executionService = new ExecutionService(riskManager, brokerClient, freshLedger);
        var logger = new LoggerFactory().CreateLogger<ExecutionController>();
        
        // Create mock configuration with Execution:Enabled = true
        var configDict = new Dictionary<string, string?>
        {
            ["Execution:Enabled"] = "true"
        };
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(configDict)
            .Build();
        
        var freshController = new ExecutionController(executionService, freshLedger, logger, configuration);

        // Act
        var result = freshController.GetLedger();

        // Assert
        var okResult = Assert.IsType<OkObjectResult>(result);
        var ledgerData = Assert.IsType<LedgerDto>(okResult.Value);
        Assert.Empty(ledgerData.Intents);
        Assert.Empty(ledgerData.Brackets);
        Assert.Empty(ledgerData.Results);
    }

    [Fact]
    public async Task ExecuteOrder_ExecutionDisabled_Returns503()
    {
        // Arrange - controller with Execution:Enabled = false
        var options = new ExecutionOptions { Enabled = true };
        var riskManager = new RiskManagerV0(options);
        var brokerClient = new FakeBrokerClient();
        var ledger = new InMemoryExecutionLedger();
        var executionService = new ExecutionService(riskManager, brokerClient, ledger);
        var logger = new LoggerFactory().CreateLogger<ExecutionController>();
        
        var configDict = new Dictionary<string, string?>
        {
            ["Execution:Enabled"] = "false"
        };
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(configDict)
            .Build();
        
        var disabledController = new ExecutionController(executionService, ledger, logger, configuration);
        
        var dto = new OrderIntentDto
        {
            Mode = "Paper",
            Symbol = "AAPL",
            Side = "Buy",
            Type = "Market",
            Quantity = 100m
        };

        // Act
        var result = await disabledController.ExecuteOrder(dto);

        // Assert
        var statusResult = Assert.IsType<ObjectResult>(result);
        Assert.Equal(503, statusResult.StatusCode);
    }

    [Fact]
    public async Task ExecuteBracket_ExecutionDisabled_Returns503()
    {
        // Arrange - controller with Execution:Enabled = false
        var options = new ExecutionOptions { Enabled = true };
        var riskManager = new RiskManagerV0(options);
        var brokerClient = new FakeBrokerClient();
        var ledger = new InMemoryExecutionLedger();
        var executionService = new ExecutionService(riskManager, brokerClient, ledger);
        var logger = new LoggerFactory().CreateLogger<ExecutionController>();
        
        var configDict = new Dictionary<string, string?>
        {
            ["Execution:Enabled"] = "false"
        };
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(configDict)
            .Build();
        
        var disabledController = new ExecutionController(executionService, ledger, logger, configuration);
        
        var dto = new BracketIntentDto
        {
            Entry = new OrderIntentDto
            {
                Mode = "Paper",
                Symbol = "TSLA",
                Side = "Buy",
                Type = "Market",
                Quantity = 50m
            },
            TakeProfit = new OrderIntentDto
            {
                Mode = "Paper",
                Symbol = "TSLA",
                Side = "Sell",
                Type = "Limit",
                Quantity = 50m,
                LimitPrice = 250m
            }
        };

        // Act
        var result = await disabledController.ExecuteBracket(dto);

        // Assert
        var statusResult = Assert.IsType<ObjectResult>(result);
        Assert.Equal(503, statusResult.StatusCode);
    }

    [Fact]
    public void GetLedger_ExecutionDisabled_Returns503()
    {
        // Arrange - controller with Execution:Enabled = false
        var options = new ExecutionOptions { Enabled = true };
        var riskManager = new RiskManagerV0(options);
        var brokerClient = new FakeBrokerClient();
        var ledger = new InMemoryExecutionLedger();
        var executionService = new ExecutionService(riskManager, brokerClient, ledger);
        var logger = new LoggerFactory().CreateLogger<ExecutionController>();
        
        var configDict = new Dictionary<string, string?>
        {
            ["Execution:Enabled"] = "false"
        };
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(configDict)
            .Build();
        
        var disabledController = new ExecutionController(executionService, ledger, logger, configuration);

        // Act
        var result = disabledController.GetLedger();

        // Assert
        var statusResult = Assert.IsType<ObjectResult>(result);
        Assert.Equal(503, statusResult.StatusCode);
    }
}
