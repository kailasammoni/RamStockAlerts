using System;
using System.Collections.Generic;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using RamStockAlerts.Engine;
using RamStockAlerts.Models;
using Xunit;

namespace RamStockAlerts.Tests;

public sealed class OrderFlowSignalValidatorCooldownTests
{
    [Fact]
    public void EvaluateDecision_BypassesCooldown_ForHighConfidence()
    {
        var metrics = new OrderFlowMetrics(NullLogger<OrderFlowMetrics>.Instance);
        var validator = CreateValidator(metrics, cooldownBypassScore: 90);
        var nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var baseTimeMs = nowMs - 1500;
        var book = BuildCandidateBook(bidSizePerLevel: 100m, askSizePerLevel: 25m, baseTimeMs, nowMs);

        metrics.UpdateMetrics(book, nowMs);
        validator.RecordAcceptedSignal(book.Symbol, nowMs - 1000);

        var decision = validator.EvaluateDecision(book, nowMs);

        Assert.True(decision.HasCandidate);
        Assert.True(decision.Accepted);
        Assert.Null(decision.RejectionReason);
        Assert.NotNull(decision.Signal);
        Assert.True(decision.Signal!.Confidence >= 90);
    }

    [Fact]
    public void EvaluateDecision_RespectsCooldown_ForLowerConfidence()
    {
        var metrics = new OrderFlowMetrics(NullLogger<OrderFlowMetrics>.Instance);
        var validator = CreateValidator(metrics, cooldownBypassScore: 90);
        var nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var baseTimeMs = nowMs - 1500;
        var book = BuildCandidateBook(bidSizePerLevel: 70m, askSizePerLevel: 25m, baseTimeMs, nowMs);

        metrics.UpdateMetrics(book, nowMs);
        validator.RecordAcceptedSignal(book.Symbol, nowMs - 1000);

        var decision = validator.EvaluateDecision(book, nowMs);

        Assert.True(decision.HasCandidate);
        Assert.False(decision.Accepted);
        Assert.Equal("CooldownActive", decision.RejectionReason);
    }

    private static OrderFlowSignalValidator CreateValidator(OrderFlowMetrics metrics, int cooldownBypassScore)
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Signals:HardGates:MaxSpoofScore"] = "0.3",
                ["Signals:HardGates:MinTapeAcceleration"] = "2.0",
                ["Signals:HardGates:MinWallPersistenceMs"] = "1000",
                ["Signals:CooldownBypassScore"] = cooldownBypassScore.ToString()
            })
            .Build();

        return new OrderFlowSignalValidator(NullLogger<OrderFlowSignalValidator>.Instance, metrics, config);
    }

    private static OrderBookState BuildCandidateBook(
        decimal bidSizePerLevel,
        decimal askSizePerLevel,
        long baseTimeMs,
        long currentTimeMs)
    {
        var book = new OrderBookState("AAPL");

        for (int i = 0; i < 4; i++)
        {
            var bidPrice = 100.00m - (i * 0.01m);
            var askPrice = 100.05m + (i * 0.01m);
            book.ApplyDepthUpdate(new DepthUpdate(book.Symbol, DepthSide.Bid, DepthOperation.Insert, bidPrice, bidSizePerLevel, i, baseTimeMs));
            book.ApplyDepthUpdate(new DepthUpdate(book.Symbol, DepthSide.Ask, DepthOperation.Insert, askPrice, askSizePerLevel, i, baseTimeMs));
        }

        book.ApplyDepthUpdate(new DepthUpdate(
            book.Symbol,
            DepthSide.Bid,
            DepthOperation.Update,
            99.97m,
            bidSizePerLevel,
            3,
            currentTimeMs));

        book.RecordTrade(currentTimeMs - 5000, currentTimeMs - 5000, 100.01, 1m);
        book.RecordTrade(currentTimeMs - 4500, currentTimeMs - 4500, 100.02, 100m);
        book.RecordTrade(currentTimeMs - 1000, currentTimeMs - 1000, 100.03, 1m);
        book.RecordTrade(currentTimeMs - 900, currentTimeMs - 900, 100.04, 1m);
        book.RecordTrade(currentTimeMs - 800, currentTimeMs - 800, 100.05, 1m);
        book.RecordTrade(currentTimeMs - 700, currentTimeMs - 700, 100.06, 1m);

        return book;
    }
}
