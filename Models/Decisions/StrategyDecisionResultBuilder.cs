using System;
using System.Collections.Generic;

namespace RamStockAlerts.Models.Decisions;

/// <summary>
/// Packages an already-computed decision into a canonical result without changing logic.
/// </summary>
public static class StrategyDecisionResultBuilder
{
    public static StrategyDecisionResult Build(StrategyDecisionBuildContext context)
    {
        var snapshot = new FeatureSnapshot
        {
            Symbol = context.Symbol ?? string.Empty,
            TimestampMs = context.TimestampMs,
            QueueImbalance = context.QueueImbalance ?? 0m,
            BidDepth4Level = context.BidDepth4Level ?? 0m,
            AskDepth4Level = context.AskDepth4Level ?? 0m,
            BidWallAgeMs = context.BidWallAgeMs ?? 0,
            AskWallAgeMs = context.AskWallAgeMs ?? 0,
            BidAbsorptionRate = context.BidAbsorptionRate ?? 0m,
            AskAbsorptionRate = context.AskAbsorptionRate ?? 0m,
            SpoofScore = context.SpoofScore ?? 0m,
            TapeAcceleration = context.TapeAcceleration ?? 0m,
            TradesIn3Sec = context.TradesIn3Sec ?? 0,
            Spread = context.Spread ?? 0m,
            MidPrice = context.MidPrice ?? 0m,
            LastPrice = context.LastPrice,
            VwapPrice = context.VwapPrice,
            BestBidPrice = context.BestBidPrice ?? 0m,
            BestBidSize = context.BestBidSize ?? 0m,
            BestAskPrice = context.BestAskPrice ?? 0m,
            BestAskSize = context.BestAskSize ?? 0m,
            TotalBidSizeTopN = context.TotalBidSizeTopN ?? 0m,
            TotalAskSizeTopN = context.TotalAskSizeTopN ?? 0m,
            BidAskRatioTopN = context.BidAskRatioTopN ?? 0m,
            TapeVelocity3Sec = context.TapeVelocity3Sec ?? 0m,
            TapeVolume3Sec = context.TapeVolume3Sec ?? 0m,
            LastDepthUpdateAgeMs = context.LastDepthUpdateAgeMs,
            LastTapeUpdateAgeMs = context.LastTapeUpdateAgeMs,
            TickerCooldownRemainingSec = context.TickerCooldownRemainingSec,
            AlertsLastHourCount = context.AlertsLastHourCount ?? 0,
            IsBookValid = context.IsBookValid,
            TapeRecent = context.TapeRecent,
            BidsTopN = context.BidsTopN ?? Array.Empty<FeatureDepthLevelSnapshot>(),
            AsksTopN = context.AsksTopN ?? Array.Empty<FeatureDepthLevelSnapshot>()
        };

        return new StrategyDecisionResult
        {
            Outcome = context.Outcome,
            Direction = context.Direction,
            Score = context.Score,
            HardRejectReasons = context.HardRejectReasons ?? Array.Empty<HardRejectReason>(),
            Snapshot = snapshot
        };
    }
}

public sealed record StrategyDecisionBuildContext
{
    public string? Symbol { get; init; }
    public long? TimestampMs { get; init; }
    public decimal Score { get; init; }
    public TradeDirection? Direction { get; init; }
    public DecisionOutcome Outcome { get; init; }
    public IReadOnlyList<HardRejectReason>? HardRejectReasons { get; init; }
    public decimal? QueueImbalance { get; init; }
    public decimal? BidDepth4Level { get; init; }
    public decimal? AskDepth4Level { get; init; }
    public long? BidWallAgeMs { get; init; }
    public long? AskWallAgeMs { get; init; }
    public decimal? BidAbsorptionRate { get; init; }
    public decimal? AskAbsorptionRate { get; init; }
    public decimal? SpoofScore { get; init; }
    public decimal? TapeAcceleration { get; init; }
    public int? TradesIn3Sec { get; init; }
    public decimal? Spread { get; init; }
    public decimal? MidPrice { get; init; }
    public decimal? BestBidPrice { get; init; }
    public decimal? BestBidSize { get; init; }
    public decimal? BestAskPrice { get; init; }
    public decimal? BestAskSize { get; init; }
    public decimal? TotalBidSizeTopN { get; init; }
    public decimal? TotalAskSizeTopN { get; init; }
    public decimal? BidAskRatioTopN { get; init; }
    public decimal? TapeVelocity3Sec { get; init; }
    public decimal? TapeVolume3Sec { get; init; }
    public decimal? LastPrice { get; init; }
    public decimal? VwapPrice { get; init; }
    public long? LastDepthUpdateAgeMs { get; init; }
    public long? LastTapeUpdateAgeMs { get; init; }
    public double? TickerCooldownRemainingSec { get; init; }
    public int? AlertsLastHourCount { get; init; }
    public bool? IsBookValid { get; init; }
    public bool? TapeRecent { get; init; }
    public IReadOnlyList<FeatureDepthLevelSnapshot>? BidsTopN { get; init; }
    public IReadOnlyList<FeatureDepthLevelSnapshot>? AsksTopN { get; init; }
}
