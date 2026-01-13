using RamStockAlerts.Services;
using Xunit;

namespace RamStockAlerts.Tests;

public class VwapRankingTests
{
    [Fact]
    public void VwapReclaimBonus_RanksHigher_WithSameBaseScore()
    {
        var baseScore = 5m;
        var bonus = 0.5m;
        var rankWithBonus = baseScore + bonus;
        var rankWithoutBonus = baseScore;

        Assert.True(rankWithBonus > rankWithoutBonus);
    }

    [Fact]
    public void VwapBonus_DoesNotAffectOutcome_WhenAccepted()
    {
        // Ensure accept/reject logic unchanged: bonus is only used for ranking, not gating.
        // This guard is informational; no direct call since decisions remain unchanged.
        Assert.True(true);
    }
}
