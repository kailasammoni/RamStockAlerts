using System;
using RamStockAlerts.Models.Decisions;
using Xunit;

namespace RamStockAlerts.Tests;

public class StrategyDecisionResultBuilderTests
{
    [Fact]
    public void Build_AcceptedDecision_PackagesSnapshot()
    {
        var context = new StrategyDecisionBuildContext
        {
            Outcome = DecisionOutcome.Accepted,
            Direction = TradeDirection.Buy,
            Score = 9.5m,
            Symbol = "AAPL",
            TimestampMs = 123,
            QueueImbalance = 3.1m,
            Spread = 0.02m,
            BestBidPrice = 10.00m,
            BestAskPrice = 10.02m,
            BidDepth4Level = 500m,
            AskDepth4Level = 200m,
            BidsTopN = new[] { new FeatureDepthLevelSnapshot(0, 10.00m, 100m) },
            AsksTopN = new[] { new FeatureDepthLevelSnapshot(0, 10.02m, 80m) }
        };

        var result = StrategyDecisionResultBuilder.Build(context);

        Assert.Equal(DecisionOutcome.Accepted, result.Outcome);
        Assert.Equal(TradeDirection.Buy, result.Direction);
        Assert.Empty(result.HardRejectReasons);
        Assert.Equal(9.5m, result.Score);
        Assert.Equal(3.1m, result.Snapshot?.QueueImbalance);
        Assert.Equal(0.02m, result.Snapshot?.Spread);
        Assert.Equal(10.00m, result.Snapshot?.BestBidPrice);
        Assert.Equal(10.02m, result.Snapshot?.BestAskPrice);
        Assert.Single(result.Snapshot?.BidsTopN ?? Array.Empty<FeatureDepthLevelSnapshot>());
    }

    [Fact]
    public void Build_NotReadyDecision_RecordsHardReject()
    {
        var context = new StrategyDecisionBuildContext
        {
            Outcome = DecisionOutcome.NotReady,
            Direction = null,
            Score = 0m,
            Symbol = "MSFT",
            TimestampMs = 456,
            HardRejectReasons = new[] { HardRejectReason.NotReadyBookInvalid }
        };

        var result = StrategyDecisionResultBuilder.Build(context);

        Assert.Equal(DecisionOutcome.NotReady, result.Outcome);
        Assert.Null(result.Direction);
        Assert.Contains(HardRejectReason.NotReadyBookInvalid, result.HardRejectReasons);
        Assert.Equal(0m, result.Score);
        Assert.Equal("MSFT", result.Snapshot?.Symbol);
    }

    [Fact]
    public void Build_RejectedDecision_MapsRejectReasons()
    {
        var context = new StrategyDecisionBuildContext
        {
            Outcome = DecisionOutcome.Rejected,
            Direction = TradeDirection.Sell,
            Score = 7m,
            Symbol = "TSLA",
            HardRejectReasons = new[] { HardRejectReason.BlueprintUnavailable },
            Spread = 0.05m
        };

        var result = StrategyDecisionResultBuilder.Build(context);

        Assert.Equal(DecisionOutcome.Rejected, result.Outcome);
        Assert.Equal(TradeDirection.Sell, result.Direction);
        Assert.Contains(HardRejectReason.BlueprintUnavailable, result.HardRejectReasons);
        Assert.Equal(7m, result.Score);
        Assert.Equal(0.05m, result.Snapshot?.Spread);
    }
}
