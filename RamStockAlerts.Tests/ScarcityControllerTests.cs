using Microsoft.Extensions.Configuration;
using RamStockAlerts.Services;

namespace RamStockAlerts.Tests;

public class ScarcityControllerTests
{
    private static ScarcityController CreateController(
        int maxBlueprintsPerDay = 2,
        int maxPerSymbolPerDay = 1,
        int globalCooldownMinutes = 0,
        int symbolCooldownMinutes = 0,
        int rankWindowSeconds = 0)
    {
        var data = new Dictionary<string, string?>
        {
            ["Scarcity:MaxBlueprintsPerDay"] = maxBlueprintsPerDay.ToString(),
            ["Scarcity:MaxPerSymbolPerDay"] = maxPerSymbolPerDay.ToString(),
            ["Scarcity:GlobalCooldownMinutes"] = globalCooldownMinutes.ToString(),
            ["Scarcity:SymbolCooldownMinutes"] = symbolCooldownMinutes.ToString(),
            ["Scarcity:RankWindowSeconds"] = rankWindowSeconds.ToString()
        };

        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(data)
            .Build();

        return new ScarcityController(config);
    }

    [Fact]
    public void EnforcesGlobalQuota()
    {
        var controller = CreateController(maxBlueprintsPerDay: 2, maxPerSymbolPerDay: 2, globalCooldownMinutes: 0);
        var baseTime = DateTimeOffset.UtcNow;

        for (int i = 0; i < 2; i++)
        {
            var decision = controller.Evaluate("AAPL", 9.5m, baseTime.ToUnixTimeMilliseconds() + i * 60_000);
            Assert.True(decision.Accepted);
            controller.RecordAcceptance("AAPL", baseTime.ToUnixTimeMilliseconds() + i * 60_000);
        }

        var thirdDecision = controller.Evaluate("MSFT", 8.1m, baseTime.ToUnixTimeMilliseconds() + 5 * 60_000);
        Assert.False(thirdDecision.Accepted);
        Assert.Equal("GlobalLimit", thirdDecision.ReasonCode);
    }

    [Fact]
    public void EnforcesPerSymbolQuota()
    {
        var controller = CreateController(maxBlueprintsPerDay: 10, maxPerSymbolPerDay: 1);
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        var first = controller.Evaluate("AAPL", 9m, now);
        Assert.True(first.Accepted);
        controller.RecordAcceptance("AAPL", now);

        var second = controller.Evaluate("AAPL", 9.1m, now + 60_000);
        Assert.False(second.Accepted);
        Assert.Equal("SymbolLimit", second.ReasonCode);
    }

    [Fact]
    public void EnforcesGlobalCooldown()
    {
        var controller = CreateController(maxPerSymbolPerDay: 10, globalCooldownMinutes: 10, symbolCooldownMinutes: 0);
        var startMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        var first = controller.Evaluate("AAPL", 9m, startMs);
        Assert.True(first.Accepted);
        controller.RecordAcceptance("AAPL", startMs);

        var globalCooldownDecision = controller.Evaluate("MSFT", 8.5m, startMs + (3 * 60_000));
        Assert.False(globalCooldownDecision.Accepted);
        Assert.Equal("GlobalCooldown", globalCooldownDecision.ReasonCode);
    }

    [Fact]
    public void EnforcesSymbolCooldown()
    {
        var controller = CreateController(maxPerSymbolPerDay: 10, globalCooldownMinutes: 0, symbolCooldownMinutes: 5);
        var startMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        var first = controller.Evaluate("AAPL", 9m, startMs);
        Assert.True(first.Accepted);
        controller.RecordAcceptance("AAPL", startMs);

        var symbolCooldownDecision = controller.Evaluate("AAPL", 7.8m, startMs + (3 * 60_000));
        Assert.False(symbolCooldownDecision.Accepted);
        Assert.Equal("SymbolCooldown", symbolCooldownDecision.ReasonCode);
    }

    [Fact]
    public void ResetsCountersOnDayRollover()
    {
        var controller = CreateController(maxBlueprintsPerDay: 1, maxPerSymbolPerDay: 1);
        var firstDay = DateTimeOffset.UtcNow;
        var dayTwo = firstDay.AddDays(1);

        var decision1 = controller.Evaluate("AAPL", 9m, firstDay.ToUnixTimeMilliseconds());
        Assert.True(decision1.Accepted);
        controller.RecordAcceptance("AAPL", firstDay.ToUnixTimeMilliseconds());

        var rejection = controller.Evaluate("AAPL", 9.1m, firstDay.ToUnixTimeMilliseconds() + 60_000);
        Assert.False(rejection.Accepted);

        var nextDay = controller.Evaluate("AAPL", 9.2m, dayTwo.ToUnixTimeMilliseconds());
        Assert.True(nextDay.Accepted);
    }

    [Fact]
    public void RankWindow_SelectsTopCandidateAndRanksOutOthers()
    {
        var controller = CreateController(
            maxBlueprintsPerDay: 5,
            maxPerSymbolPerDay: 5,
            globalCooldownMinutes: 1,
            symbolCooldownMinutes: 0,
            rankWindowSeconds: 2);

        var baseTime = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var firstId = Guid.NewGuid();
        var secondId = Guid.NewGuid();
        var thirdId = Guid.NewGuid();

        var decisions = new List<RankedScarcityDecision>();
        decisions.AddRange(controller.StageCandidate(firstId, "AAPL", 9.5m, baseTime));
        decisions.AddRange(controller.StageCandidate(secondId, "AAPL", 8.5m, baseTime + 500));
        decisions.AddRange(controller.StageCandidate(thirdId, "AAPL", 7.2m, baseTime + 1000));
        decisions.AddRange(controller.FlushRankWindow(baseTime + 3000, force: true));
        Assert.Equal(3, decisions.Count);

        Assert.Equal(firstId, Assert.Single(decisions, d => d.Decision.Accepted).CandidateId);

        var secondDecision = decisions.First(d => d.CandidateId == secondId);
        Assert.False(secondDecision.Decision.Accepted);
        Assert.Equal("GlobalCooldown", secondDecision.Decision.ReasonCode);

        var rankedOut = decisions.First(d => d.CandidateId == thirdId);
        Assert.False(rankedOut.Decision.Accepted);
        Assert.Equal("RejectedRankedOut", rankedOut.Decision.ReasonCode);
    }

    [Fact]
    public void RankWindow_IsDeterministicWithTies()
    {
        var controller = CreateController(
            maxBlueprintsPerDay: 5,
            maxPerSymbolPerDay: 5,
            globalCooldownMinutes: 1,
            symbolCooldownMinutes: 0,
            rankWindowSeconds: 2);

        const long baseTime = 1_000_000;
        var laterId = Guid.NewGuid();
        var earlierId = Guid.NewGuid();

        var decisions = new List<RankedScarcityDecision>();
        decisions.AddRange(controller.StageCandidate(laterId, "AAPL", 9m, baseTime + 1500));
        decisions.AddRange(controller.StageCandidate(earlierId, "MSFT", 9m, baseTime + 500));
        decisions.AddRange(controller.FlushRankWindow(baseTime + 4000, force: true));

        Assert.Equal(earlierId, Assert.Single(decisions, d => d.Decision.Accepted).CandidateId);

        var rejected = decisions.First(d => d.CandidateId == laterId);
        Assert.Equal("GlobalCooldown", rejected.Decision.ReasonCode);
    }
}
