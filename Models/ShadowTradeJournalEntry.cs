using RamStockAlerts.Models.Decisions;

namespace RamStockAlerts.Models;

public sealed class ShadowTradeJournalEntry
{
    public int SchemaVersion { get; set; }
    public Guid DecisionId { get; set; }
    public Guid SessionId { get; set; }
    public string? Source { get; set; }
    public string? EntryType { get; set; }

    public DateTimeOffset? MarketTimestampUtc { get; set; }
    public DateTimeOffset? DecisionTimestampUtc { get; set; }
    public DateTimeOffset? JournalWriteTimestampUtc { get; set; }

    public string? TradingMode { get; set; }
    public string? Symbol { get; set; }
    public string? Direction { get; set; }

    public ObservedMetricsSnapshot? ObservedMetrics { get; set; }
    public DecisionInputsSnapshot? DecisionInputs { get; set; }

    public string? DecisionOutcome { get; set; }
    public List<string>? DecisionTrace { get; set; }
    public string? RejectionReason { get; set; }
    public List<string>? DataQualityFlags { get; set; }

    public BlueprintPlan? Blueprint { get; set; }
    public SystemMetricsSnapshot? SystemMetrics { get; set; }
    public StrategyDecisionResult? DecisionResult { get; set; }

    public sealed class BlueprintPlan
    {
        public decimal? Entry { get; set; }
        public decimal? Stop { get; set; }
        public decimal? Target { get; set; }
    }

    public sealed class SystemMetricsSnapshot
    {
        public int? UniverseCount { get; set; }
        public int? ActiveSubscriptionsCount { get; set; }
        public int? DepthEnabledCount { get; set; }
        public int? TickByTickEnabledCount { get; set; }
        public long? DepthSubscribeAttempts { get; set; }
        public long? DepthSubscribeSuccess { get; set; }
        public long? DepthSubscribeFailures { get; set; }
        public long? DepthSubscribeUpdateReceived { get; set; }
        public long? DepthSubscribeErrors { get; set; }
        public Dictionary<int, int>? DepthSubscribeErrorsByCode { get; set; }
        public int? DepthSubscribeLastErrorCode { get; set; }
        public string? DepthSubscribeLastErrorMessage { get; set; }
        public bool? IsBookValidAny { get; set; }
        public bool? TapeRecentAny { get; set; }
        public long? LastDepthUpdateAgeMs { get; set; }
        public long? LastTapeUpdateAgeMs { get; set; }
    }

    public sealed class DecisionInputsSnapshot
    {
        public decimal? Score { get; set; }
        public decimal? VwapBonus { get; set; }
        public decimal? RankScore { get; set; }
        public double? TickerCooldownRemainingSec { get; set; }
        public int? AlertsLastHourCount { get; set; }
        public decimal? QueueImbalance { get; set; }
        public long? BidWallAgeMs { get; set; }
        public long? AskWallAgeMs { get; set; }
        public decimal? BidAbsorptionRate { get; set; }
        public decimal? AskAbsorptionRate { get; set; }
        public decimal? SpoofScore { get; set; }
        public decimal? TapeAcceleration { get; set; }
        public int? TradesIn3Sec { get; set; }
        public decimal? TapeVolume3Sec { get; set; }
        public decimal? Spread { get; set; }
        public decimal? BestBidPrice { get; set; }
        public decimal? BestAskPrice { get; set; }
        public DepthDeltaMetrics? DepthDelta { get; set; }
        public bool? VwapReclaimDetected { get; set; }
    }

    public sealed class ObservedMetricsSnapshot
    {
        public decimal? QueueImbalance { get; set; }
        public decimal? BidDepth4Level { get; set; }
        public decimal? AskDepth4Level { get; set; }
        public long? BidWallAgeMs { get; set; }
        public long? AskWallAgeMs { get; set; }
        public decimal? BidAbsorptionRate { get; set; }
        public decimal? AskAbsorptionRate { get; set; }
        public decimal? SpoofScore { get; set; }
        public decimal? TapeAcceleration { get; set; }
        public int? TradesIn3Sec { get; set; }
        public decimal? Spread { get; set; }
        public decimal? MidPrice { get; set; }
        public decimal? LastPrice { get; set; }
        public decimal? VwapPrice { get; set; }
        public decimal? BestBidPrice { get; set; }
        public decimal? BestBidSize { get; set; }
        public decimal? BestAskPrice { get; set; }
        public decimal? BestAskSize { get; set; }
        public decimal? TotalBidSizeTopN { get; set; }
        public decimal? TotalAskSizeTopN { get; set; }
        public decimal? BidAskRatioTopN { get; set; }
        public decimal? TapeVelocity3Sec { get; set; }
        public decimal? TapeVolume3Sec { get; set; }
        public long? LastDepthUpdateAgeMs { get; set; }
        public long? LastTapeUpdateAgeMs { get; set; }
        public decimal? CumulativeVwap { get; set; }
        public decimal? PriceVsVwap { get; set; }
        public bool? VwapReclaimDetected { get; set; }
        public DepthDeltaMetrics? DepthDelta { get; set; }
        public List<DepthLevelSnapshot>? BidsTopN { get; set; }
        public List<DepthLevelSnapshot>? AsksTopN { get; set; }
    }

    public sealed class DepthDeltaMetrics
    {
        public decimal? BidCancelToAddRatio1s { get; set; }
        public decimal? AskCancelToAddRatio1s { get; set; }
        public decimal? BidCancelToAddRatio3s { get; set; }
        public decimal? AskCancelToAddRatio3s { get; set; }
        public int? BidCancelCount1s { get; set; }
        public int? BidAddCount1s { get; set; }
        public int? AskCancelCount1s { get; set; }
        public int? AskAddCount1s { get; set; }
        public decimal? BidTotalCanceledSize1s { get; set; }
        public decimal? AskTotalCanceledSize1s { get; set; }
        public decimal? BidTotalAddedSize1s { get; set; }
        public decimal? AskTotalAddedSize1s { get; set; }
    }

    public sealed class DepthLevelSnapshot
    {
        public int Level { get; set; }
        public decimal Price { get; set; }
        public decimal Size { get; set; }
    }
}
