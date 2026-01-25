using RamStockAlerts.Models.Decisions;
using Xunit;

namespace RamStockAlerts.Tests;

public class VwapPackagingTests
{
    [Fact]
    public void Builder_PackagesVwapFields_WithoutChangingOutcome()
    {
        var context = new StrategyDecisionBuildContext
        {
            Outcome = DecisionOutcome.Accepted,
            HardRejectReasons = System.Array.Empty<HardRejectReason>(),
            CurrentVwap = 10.5m,
            PriceVsVwap = 0.2m,
            VwapReclaimDetected = true
        };

        var result = StrategyDecisionResultBuilder.Build(context);

        Assert.Equal(DecisionOutcome.Accepted, result.Outcome);
        Assert.Empty(result.HardRejectReasons);
        Assert.Equal(10.5m, result.Snapshot?.CurrentVwap);
        Assert.Equal(0.2m, result.Snapshot?.PriceVsVwap);
        Assert.True(result.Snapshot?.VwapReclaimDetected);
    }
}
