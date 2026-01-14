using RamStockAlerts.Models;
using RamStockAlerts.Services;
using Xunit;

namespace RamStockAlerts.Tests;

public class ShadowTradingHelpersTapeStatusTests
{
    [Fact]
    public void TapeEnabled_NoTrades_IsNotWarmedUp()
    {
        var config = ShadowTradingHelpers.TapeGateConfig.Default;
        var nowMs = 100_000L;
        var book = new OrderBookState("TEST");

        var status = ShadowTradingHelpers.GetTapeStatus(book, nowMs, isTapeEnabled: true, config);

        Assert.Equal(ShadowTradingHelpers.TapeStatusKind.NotWarmedUp, status.Kind);
    }

    [Fact]
    public void TapeEnabled_OldTradeBeyondStale_IsStale()
    {
        var config = new ShadowTradingHelpers.TapeGateConfig(warmupMinTrades: 1, warmupWindowMs: 60000, staleWindowMs: 30000);
        var nowMs = 100_000L;
        var book = new OrderBookState("TEST");
        book.RecordTrade(nowMs - 40_000, 100.10, 1m);

        var status = ShadowTradingHelpers.GetTapeStatus(book, nowMs, isTapeEnabled: true, config);

        Assert.Equal(ShadowTradingHelpers.TapeStatusKind.Stale, status.Kind);
    }

    [Fact]
    public void TapeEnabled_RecentTradeWithinWarmup_IsReady()
    {
        var config = ShadowTradingHelpers.TapeGateConfig.Default;
        var nowMs = 100_000L;
        var book = new OrderBookState("TEST");
        book.RecordTrade(nowMs - 1_000, 100.10, 1m);

        var status = ShadowTradingHelpers.GetTapeStatus(book, nowMs, isTapeEnabled: true, config);

        Assert.Equal(ShadowTradingHelpers.TapeStatusKind.Ready, status.Kind);
    }
}
