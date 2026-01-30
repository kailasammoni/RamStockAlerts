using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Extensions.Configuration;
using RamStockAlerts.Services;
using Xunit;

namespace RamStockAlerts.Tests;

public sealed class ScarcityControllerTests
{
    [Fact]
    public void StageCandidate_AllowsSameSymbolAfterGap()
    {
        var config = BuildConfig(new Dictionary<string, string?>
        {
            ["Scarcity:MaxBlueprintsPerDay"] = "50",
            ["Scarcity:MaxPerSymbolPerDay"] = "10",
            ["Scarcity:GlobalCooldownMinutes"] = "0",
            ["Scarcity:PerSymbolCooldownMinutes"] = "5"
        });
        var controller = new ScarcityController(config);
        var baseMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        var first = controller.StageCandidate(Guid.NewGuid(), "AAPL", 1.0m, baseMs).Single().Decision;
        Assert.True(first.Accepted);

        var tooSoon = controller.StageCandidate(Guid.NewGuid(), "AAPL", 1.0m, baseMs + 4 * 60_000).Single().Decision;
        Assert.False(tooSoon.Accepted);
        Assert.Equal("SymbolCooldown", tooSoon.ReasonCode);

        var afterGap = controller.StageCandidate(Guid.NewGuid(), "AAPL", 1.0m, baseMs + 5 * 60_000).Single().Decision;
        Assert.True(afterGap.Accepted);
    }

    [Fact]
    public void StageCandidate_RejectsSameSymbolWithinGap()
    {
        var config = BuildConfig(new Dictionary<string, string?>
        {
            ["Scarcity:MaxBlueprintsPerDay"] = "50",
            ["Scarcity:MaxPerSymbolPerDay"] = "10",
            ["Scarcity:GlobalCooldownMinutes"] = "0",
            ["Scarcity:PerSymbolCooldownMinutes"] = "15"
        });
        var controller = new ScarcityController(config);
        var baseMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        var first = controller.StageCandidate(Guid.NewGuid(), "MSFT", 1.0m, baseMs).Single().Decision;
        Assert.True(first.Accepted);

        var tooSoon = controller.StageCandidate(Guid.NewGuid(), "MSFT", 1.0m, baseMs + 10 * 60_000).Single().Decision;
        Assert.False(tooSoon.Accepted);
        Assert.Equal("SymbolCooldown", tooSoon.ReasonCode);
    }

    [Fact]
    public void StageCandidate_EnforcesDailySymbolLimitAfterCooldown()
    {
        var config = BuildConfig(new Dictionary<string, string?>
        {
            ["Scarcity:MaxBlueprintsPerDay"] = "50",
            ["Scarcity:MaxPerSymbolPerDay"] = "1",
            ["Scarcity:GlobalCooldownMinutes"] = "0",
            ["Scarcity:PerSymbolCooldownMinutes"] = "1"
        });
        var controller = new ScarcityController(config);
        var baseMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        var first = controller.StageCandidate(Guid.NewGuid(), "NVDA", 1.0m, baseMs).Single().Decision;
        Assert.True(first.Accepted);

        var afterGap = controller.StageCandidate(Guid.NewGuid(), "NVDA", 1.0m, baseMs + 2 * 60_000).Single().Decision;
        Assert.False(afterGap.Accepted);
        Assert.Equal("SymbolLimit", afterGap.ReasonCode);
    }

    [Fact]
    public void RecordCancelledAcceptance_UsesShorterGlobalCooldown()
    {
        var config = BuildConfig(new Dictionary<string, string?>
        {
            ["Scarcity:MaxBlueprintsPerDay"] = "50",
            ["Scarcity:MaxPerSymbolPerDay"] = "10",
            ["Scarcity:GlobalCooldownMinutes"] = "45",
            ["Scarcity:CancelledCooldownMinutes"] = "2",
            ["Scarcity:PerSymbolCooldownMinutes"] = "0"
        });
        var controller = new ScarcityController(config);
        var baseMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        var first = controller.StageCandidate(Guid.NewGuid(), "TSLA", 1.0m, baseMs).Single().Decision;
        Assert.True(first.Accepted);

        controller.RecordCancelledAcceptance("TSLA", baseMs + 10_000);

        var tooSoon = controller.StageCandidate(Guid.NewGuid(), "GITS", 1.0m, baseMs + 60_000).Single().Decision;
        Assert.False(tooSoon.Accepted);
        Assert.Equal("CancelledCooldown", tooSoon.ReasonCode);

        var afterShortGap = controller.StageCandidate(Guid.NewGuid(), "FEED", 1.0m, baseMs + 3 * 60_000).Single().Decision;
        Assert.True(afterShortGap.Accepted);
    }

    private static IConfiguration BuildConfig(Dictionary<string, string?> settings)
    {
        return new ConfigurationBuilder()
            .AddInMemoryCollection(settings)
            .Build();
    }
}
