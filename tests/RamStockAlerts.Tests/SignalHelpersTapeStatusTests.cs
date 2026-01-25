using RamStockAlerts.Models;
using RamStockAlerts.Services.Signals;
using Xunit;

namespace RamStockAlerts.Tests;

public class SignalHelpersTapeStatusTests
{
    [Fact]
    public void TapeEnabled_NoTrades_IsNotWarmedUp()
    {
        var config = SignalHelpers.TapeGateConfig.Default;
        var nowMs = 100_000L;
        var book = new OrderBookState("TEST");

        var status = SignalHelpers.GetTapeStatus(book, nowMs, isTapeEnabled: true, config);

        Assert.Equal(SignalHelpers.TapeStatusKind.NotWarmedUp, status.Kind);
        Assert.Null(status.AgeMs);
    }

    [Fact]
    public void TapeEnabled_OldTradeBeyondStale_IsStale()
    {
        var config = new SignalHelpers.TapeGateConfig(WarmupMinTrades: 1, WarmupWindowMs: 60000, StaleWindowMs: 30000);
        var nowMs = 100_000L;
        var book = new OrderBookState("TEST");
        book.RecordTrade(nowMs - 40_000, nowMs - 40_000, 100.10, 1m);

        var status = SignalHelpers.GetTapeStatus(book, nowMs, isTapeEnabled: true, config);

        Assert.Equal(SignalHelpers.TapeStatusKind.Stale, status.Kind);
    }

    [Fact]
    public void TapeEnabled_RecentTradeWithinWarmup_IsReady()
    {
        var config = SignalHelpers.TapeGateConfig.Default;
        var nowMs = 100_000L;
        var book = new OrderBookState("TEST");
        book.RecordTrade(nowMs - 1_000, nowMs - 1_000, 100.10, 1m);

        var status = SignalHelpers.GetTapeStatus(book, nowMs, isTapeEnabled: true, config);

        Assert.Equal(SignalHelpers.TapeStatusKind.Ready, status.Kind);
    }
}


