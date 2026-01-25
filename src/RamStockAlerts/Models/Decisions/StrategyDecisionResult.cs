using System;
using System.Collections.Generic;

namespace RamStockAlerts.Models.Decisions;

public enum DecisionOutcome
{
    Accepted,
    Rejected,
    NotReady
}

public enum TradeDirection
{
    Buy,
    Sell
}

public enum HardRejectReason
{
    Unknown = 0,
    NotReadyBookInvalid,
    NotReadyTapeStale,
    NotReadyTapeMissingSubscription,
    NotReadyTapeNotWarmedUp,
    NotReadyNoDepth,
    CooldownActive,
    GlobalRateLimit,
    BlueprintUnavailable,
    ScarcityGlobalLimit,
    ScarcitySymbolLimit,
    ScarcityGlobalCooldown,
    ScarcitySymbolCooldown,
    ScarcityRankedOut,
    SpoofSuspected,
    TapeAccelerationInsufficient,
    WallPersistenceInsufficient,
    ReplenishmentSuspected,
    AbsorptionInsufficient
}

public sealed record FeatureDepthLevelSnapshot(int Level, decimal Price, decimal Size);

public sealed record FeatureSnapshot
{
    public string Symbol { get; init; } = string.Empty;
    public long? TimestampMs { get; init; }
    public decimal QueueImbalance { get; init; }
    public decimal BidDepth4Level { get; init; }
    public decimal AskDepth4Level { get; init; }
    public long BidWallAgeMs { get; init; }
    public long AskWallAgeMs { get; init; }
    public decimal BidAbsorptionRate { get; init; }
    public decimal AskAbsorptionRate { get; init; }
    public decimal SpoofScore { get; init; }
    public decimal TapeAcceleration { get; init; }
    public int TradesIn3Sec { get; init; }
    public int BidTradesIn3Sec { get; init; }
    public int AskTradesIn3Sec { get; init; }
    public decimal Spread { get; init; }
    public decimal MidPrice { get; init; }
    public decimal? LastPrice { get; init; }
    public decimal? VwapPrice { get; init; }
    public decimal BestBidPrice { get; init; }
    public decimal BestBidSize { get; init; }
    public decimal BestAskPrice { get; init; }
    public decimal BestAskSize { get; init; }
    public decimal TotalBidSizeTopN { get; init; }
    public decimal TotalAskSizeTopN { get; init; }
    public decimal BidAskRatioTopN { get; init; }
    public decimal TapeVelocity3Sec { get; init; }
    public decimal TapeVolume3Sec { get; init; }
    public long? LastDepthUpdateAgeMs { get; init; }
    public long? LastTapeUpdateAgeMs { get; init; }
    public double? TickerCooldownRemainingSec { get; init; }
    public int AlertsLastHourCount { get; init; }
    public bool? IsBookValid { get; init; }
    public bool? TapeRecent { get; init; }
    public IReadOnlyList<FeatureDepthLevelSnapshot> BidsTopN { get; init; } = Array.Empty<FeatureDepthLevelSnapshot>();
    public IReadOnlyList<FeatureDepthLevelSnapshot> AsksTopN { get; init; } = Array.Empty<FeatureDepthLevelSnapshot>();
    public decimal BidCancelToAddRatio1s { get; init; }
    public decimal AskCancelToAddRatio1s { get; init; }
    public decimal BidCancelToAddRatio3s { get; init; }
    public decimal AskCancelToAddRatio3s { get; init; }
    public int BidCancelCount1s { get; init; }
    public int BidAddCount1s { get; init; }
    public int AskCancelCount1s { get; init; }
    public int AskAddCount1s { get; init; }
    public decimal BidTotalCanceledSize1s { get; init; }
    public decimal AskTotalCanceledSize1s { get; init; }
    public decimal BidTotalAddedSize1s { get; init; }
    public decimal AskTotalAddedSize1s { get; init; }
    public decimal CurrentVwap { get; init; }
    public decimal PriceVsVwap { get; init; }
    public bool VwapReclaimDetected { get; init; }
    public decimal VwapConfirmBonus { get; init; }
}

public sealed record StrategyDecisionResult
{
    public IReadOnlyList<HardRejectReason> HardRejectReasons { get; init; } = Array.Empty<HardRejectReason>();
    public decimal Score { get; init; }
    public TradeDirection? Direction { get; init; }
    public DecisionOutcome Outcome { get; init; }
    public FeatureSnapshot? Snapshot { get; init; }
}
