namespace RamStockAlerts.Models;

public sealed class ShadowTradeJournalEntry
{
    public int SchemaVersion { get; set; } = 1;
    public Guid DecisionId { get; set; }
    public Guid SessionId { get; set; }
    public string Source { get; set; } = "IBKR";
    public string EntryType { get; set; } = "Signal";

    public DateTime TimestampUtc { get; set; }
    public string TradingMode { get; set; } = string.Empty;
    public string Symbol { get; set; } = string.Empty;
    public string Direction { get; set; } = string.Empty;
    public decimal Score { get; set; }

    public decimal QueueImbalance { get; set; }
    public decimal BidDepth4Level { get; set; }
    public decimal AskDepth4Level { get; set; }
    public long BidWallAgeMs { get; set; }
    public long AskWallAgeMs { get; set; }
    public decimal BidAbsorptionRate { get; set; }
    public decimal AskAbsorptionRate { get; set; }
    public decimal SpoofScore { get; set; }
    public decimal TapeAcceleration { get; set; }
    public int TradesIn3Sec { get; set; }
    public decimal Spread { get; set; }
    public decimal MidPrice { get; set; }
    public decimal? LastPrice { get; set; }
    public decimal? VwapPrice { get; set; }

    public decimal BestBidPrice { get; set; }
    public decimal BestBidSize { get; set; }
    public decimal BestAskPrice { get; set; }
    public decimal BestAskSize { get; set; }

    public decimal TotalBidSizeTopN { get; set; }
    public decimal TotalAskSizeTopN { get; set; }
    public decimal BidAskRatioTopN { get; set; }

    public decimal TapeVelocity3Sec { get; set; }
    public decimal TapeVolume3Sec { get; set; }

    public long? LastDepthUpdateAgeMs { get; set; }
    public long? LastTapeUpdateAgeMs { get; set; }
    public double? TickerCooldownRemainingSec { get; set; }
    public int AlertsLastHourCount { get; set; }

    public decimal? Entry { get; set; }
    public decimal? Stop { get; set; }
    public decimal? Target { get; set; }

    public bool Accepted { get; set; }
    public string Decision { get; set; } = string.Empty;
    public string? RejectionReason { get; set; }

    public int UniverseCount { get; set; }
    public int ActiveSubscriptionsCount { get; set; }
    public int DepthEnabledCount { get; set; }
    public int TickByTickEnabledCount { get; set; }
    public bool? IsBookValidAny { get; set; }
    public bool? TapeRecentAny { get; set; }

    public List<DepthLevelSnapshot> BidsTopN { get; set; } = new();
    public List<DepthLevelSnapshot> AsksTopN { get; set; } = new();

    public sealed class DepthLevelSnapshot
    {
        public int Level { get; set; }
        public decimal Price { get; set; }
        public decimal Size { get; set; }
    }
}
