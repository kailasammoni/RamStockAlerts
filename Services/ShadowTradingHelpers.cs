using System;
using System.Linq;
using RamStockAlerts.Models;

namespace RamStockAlerts.Services;

internal static class ShadowTradingHelpers
{
    internal readonly record struct TapeGateConfig(int WarmupMinTrades, int WarmupWindowMs, int StaleWindowMs)
    {
        public static TapeGateConfig Default { get; } = new(1, 15000, 30000);
    }

    internal enum TapeStatusKind
    {
        MissingSubscription,
        NotWarmedUp,
        Stale,
        Ready
    }

    internal readonly record struct TapeStatus(TapeStatusKind Kind, long? AgeMs)
    {
        public bool IsReady => Kind == TapeStatusKind.Ready;
    }

    public static bool HasRecentTape(OrderBookState book, long nowMs)
    {
        return GetTapeStatus(book, nowMs, isTapeEnabled: true, TapeGateConfig.Default).IsReady;
    }

    public static TapeStatus GetTapeStatus(
        OrderBookState book,
        long nowMs,
        bool isTapeEnabled,
        TapeGateConfig config)
    {
        if (!isTapeEnabled)
        {
            return new TapeStatus(TapeStatusKind.MissingSubscription, null);
        }

        var lastTrade = book.RecentTrades.LastOrDefault();
        if (lastTrade.TimestampMs == 0)
        {
            return new TapeStatus(TapeStatusKind.NotWarmedUp, null);
        }

        var ageMs = nowMs - lastTrade.TimestampMs;

        var warmupStart = nowMs - Math.Max(0, config.WarmupWindowMs);
        var tradesInWarmupWindow = book.RecentTrades.Count(trade => trade.TimestampMs >= warmupStart);
        if (tradesInWarmupWindow < Math.Max(0, config.WarmupMinTrades))
        {
            return new TapeStatus(TapeStatusKind.NotWarmedUp, ageMs);
        }

        if (ageMs > Math.Max(0, config.StaleWindowMs))
        {
            return new TapeStatus(TapeStatusKind.Stale, ageMs);
        }

        return new TapeStatus(TapeStatusKind.Ready, ageMs);
    }
}
