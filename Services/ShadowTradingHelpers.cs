using RamStockAlerts.Models;

namespace RamStockAlerts.Services;

internal static class ShadowTradingHelpers
{
    private const int TapePresenceWindowMs = 3000;

    public static bool HasRecentTape(OrderBookState book, long nowMs)
    {
        if (book.RecentTrades.Count == 0)
        {
            return false;
        }

        var lastTrade = book.RecentTrades.LastOrDefault();
        if (lastTrade.TimestampMs == 0)
        {
            return false;
        }

        return nowMs - lastTrade.TimestampMs <= TapePresenceWindowMs;
    }
}
