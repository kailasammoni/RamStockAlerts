using System;
using System.Linq;
using Microsoft.Extensions.Configuration;
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

    internal readonly record struct TapeStatus(
        TapeStatusKind Kind,
        long? AgeMs,
        int TradesInWarmupWindow,
        int WarmupMinTrades,
        int WarmupWindowMs)
    {
        public bool IsReady => Kind == TapeStatusKind.Ready;
    }

    public static bool HasRecentTape(OrderBookState book, long nowMs, TapeGateConfig config)
    {
        return GetTapeStatus(book, nowMs, isTapeEnabled: true, config).IsReady;
    }

    public static TapeStatus GetTapeStatus(
        OrderBookState book,
        long nowMs,
        bool isTapeEnabled,
        TapeGateConfig config)
    {
        var warmupMinTrades = Math.Max(0, config.WarmupMinTrades);
        var warmupWindowMs = Math.Max(0, config.WarmupWindowMs);

        if (!isTapeEnabled)
        {
            return new TapeStatus(TapeStatusKind.MissingSubscription, null, 0, warmupMinTrades, warmupWindowMs);
        }

        if (book.RecentTrades.Count == 0)
        {
            return new TapeStatus(TapeStatusKind.NotWarmedUp, null, 0, warmupMinTrades, warmupWindowMs);
        }

        // Use RECEIPT TIME for staleness check, not event time
        // Event time can lag due to IB server delays, batching, replay, etc.
        var lastTapeRecvMs = book.LastTapeRecvMs;
        if (lastTapeRecvMs == 0)
        {
            return new TapeStatus(TapeStatusKind.NotWarmedUp, null, 0, warmupMinTrades, warmupWindowMs);
        }

        var ageMs = nowMs - lastTapeRecvMs;
        var staleWindowMs = Math.Max(0, config.StaleWindowMs);

        if (ageMs > staleWindowMs)
        {
            return new TapeStatus(TapeStatusKind.Stale, ageMs, 0, warmupMinTrades, warmupWindowMs);
        }

        // Count trades in warmup window based on RECEIPT TIME, not event time
        var warmupStart = nowMs - warmupWindowMs;
        var tradesInWarmupWindow = book.RecentTrades.Count(trade => trade.ReceiptTimestampMs >= warmupStart);
        if (tradesInWarmupWindow < warmupMinTrades)
        {
            return new TapeStatus(
                TapeStatusKind.NotWarmedUp,
                ageMs,
                tradesInWarmupWindow,
                warmupMinTrades,
                warmupWindowMs);
        }

        return new TapeStatus(TapeStatusKind.Ready, ageMs, tradesInWarmupWindow, warmupMinTrades, warmupWindowMs);
    }

    public static TapeGateConfig ReadTapeGateConfig(IConfiguration configuration)
    {
        var defaults = TapeGateConfig.Default;
        var warmupMinTrades = configuration.GetValue("MarketData:TapeWarmupMinTrades", defaults.WarmupMinTrades);
        var warmupWindowMs = configuration.GetValue("MarketData:TapeWarmupWindowMs", defaults.WarmupWindowMs);
        var staleWindowMs = configuration.GetValue("MarketData:TapeStaleWindowMs", defaults.StaleWindowMs);

        return new TapeGateConfig(
            Math.Max(0, warmupMinTrades),
            Math.Max(0, warmupWindowMs),
            Math.Max(0, staleWindowMs));
    }
}
