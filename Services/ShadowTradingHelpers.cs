using RamStockAlerts.Models;

namespace RamStockAlerts.Services;

internal static class ShadowTradingHelpers
{
    private const int TapePresenceWindowMs = 3000;

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
        return GetTapeStatus(book, nowMs, isTapeEnabled: true).IsReady;
    }

    public static TapeStatus GetTapeStatus(OrderBookState book, long nowMs, bool isTapeEnabled)
    {
        if (!isTapeEnabled)
        {
            return new TapeStatus(TapeStatusKind.MissingSubscription, null);
        }

        if (book.RecentTrades.Count == 0)
        {
            return new TapeStatus(TapeStatusKind.NotWarmedUp, null);
        }

        var lastTrade = book.RecentTrades.LastOrDefault();
        if (lastTrade.TimestampMs == 0)
        {
            return new TapeStatus(TapeStatusKind.NotWarmedUp, null);
        }

        var ageMs = nowMs - lastTrade.TimestampMs;
        if (ageMs > TapePresenceWindowMs)
        {
            return new TapeStatus(TapeStatusKind.Stale, ageMs);
        }

        return new TapeStatus(TapeStatusKind.Ready, ageMs);
    }
}
