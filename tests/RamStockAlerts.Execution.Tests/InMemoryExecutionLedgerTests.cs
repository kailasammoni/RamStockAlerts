namespace RamStockAlerts.Execution.Tests;

using RamStockAlerts.Execution.Contracts;
using RamStockAlerts.Execution.Storage;
using Xunit;

public class InMemoryExecutionLedgerTests
{
    [Fact]
    public void GetSubmittedIntentCountToday_ExcludesRejected()
    {
        var ledger = new InMemoryExecutionLedger();
        var intent = CreateIntent();

        ledger.RecordIntent(intent);
        ledger.RecordResult(intent.IntentId, new ExecutionResult
        {
            IntentId = intent.IntentId,
            Status = ExecutionStatus.Rejected,
            TimestampUtc = DateTimeOffset.UtcNow
        });

        Assert.Equal(0, ledger.GetSubmittedIntentCountToday(DateTimeOffset.UtcNow));
    }

    [Fact]
    public void GetSubmittedIntentCountToday_IncludesAccepted()
    {
        var ledger = new InMemoryExecutionLedger();
        var intent = CreateIntent();

        ledger.RecordIntent(intent);
        ledger.RecordResult(intent.IntentId, new ExecutionResult
        {
            IntentId = intent.IntentId,
            Status = ExecutionStatus.Accepted,
            TimestampUtc = DateTimeOffset.UtcNow
        });

        Assert.Equal(1, ledger.GetSubmittedIntentCountToday(DateTimeOffset.UtcNow));
    }

    [Fact]
    public void GetOpenBracketCount_TracksLifecycle()
    {
        var ledger = new InMemoryExecutionLedger();
        var bracket = CreateBracket();

        ledger.RecordBracket(bracket);
        ledger.RecordResult(bracket.Entry.IntentId, new ExecutionResult
        {
            IntentId = bracket.Entry.IntentId,
            Status = ExecutionStatus.Submitted,
            TimestampUtc = DateTimeOffset.UtcNow
        });

        Assert.Equal(0, ledger.GetOpenBracketCount());

        ledger.UpdateBracketState(bracket.Entry.IntentId, BracketState.Open);
        Assert.Equal(1, ledger.GetOpenBracketCount());

        ledger.UpdateBracketState(bracket.Entry.IntentId, BracketState.ClosedWin);
        Assert.Equal(0, ledger.GetOpenBracketCount());
    }

    private static OrderIntent CreateIntent()
    {
        return new OrderIntent
        {
            IntentId = Guid.NewGuid(),
            Symbol = "AAPL",
            Side = OrderSide.Buy,
            Type = OrderType.Market,
            Quantity = 10m,
            CreatedUtc = DateTimeOffset.UtcNow
        };
    }

    private static BracketIntent CreateBracket()
    {
        var entry = CreateIntent();
        return new BracketIntent
        {
            Entry = entry,
            StopLoss = new OrderIntent
            {
                IntentId = Guid.NewGuid(),
                Symbol = entry.Symbol,
                Side = OrderSide.Sell,
                Type = OrderType.Stop,
                Quantity = entry.Quantity,
                StopPrice = 99m,
                CreatedUtc = DateTimeOffset.UtcNow
            },
            TakeProfit = new OrderIntent
            {
                IntentId = Guid.NewGuid(),
                Symbol = entry.Symbol,
                Side = OrderSide.Sell,
                Type = OrderType.Limit,
                Quantity = entry.Quantity,
                LimitPrice = 105m,
                CreatedUtc = DateTimeOffset.UtcNow
            }
        };
    }
}
