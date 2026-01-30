using System.Collections.Generic;
using Microsoft.AspNetCore.Mvc;
using RamStockAlerts.Controllers;
using RamStockAlerts.Models;
using RamStockAlerts.Services;
using Xunit;

namespace RamStockAlerts.Tests;

public sealed class MetricsControllerTests
{
    [Fact]
    public async Task GetTodayMetrics_ReturnsMetrics()
    {
        var expected = new PerformanceMetrics { WinCount = 1, LossCount = 2, TotalPnlUsd = 42m };
        var controller = new MetricsController(new StubAggregator(expected, new List<OutcomeSummary>()));

        var result = await controller.GetTodayMetrics(CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var metrics = Assert.IsType<PerformanceMetrics>(ok.Value);
        Assert.Equal(1, metrics.WinCount);
        Assert.Equal(2, metrics.LossCount);
        Assert.Equal(42m, metrics.TotalPnlUsd);
    }

    [Fact]
    public async Task GetHistory_ReturnsSummaries()
    {
        var summaries = new List<OutcomeSummary>
        {
            new OutcomeSummary { DateUtc = DateOnly.FromDateTime(DateTime.UtcNow), TotalSignals = 3 }
        };
        var controller = new MetricsController(new StubAggregator(new PerformanceMetrics(), summaries));

        var result = await controller.GetHistory(days: 7, cancellationToken: CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var returned = Assert.IsType<List<OutcomeSummary>>(ok.Value);
        Assert.Single(returned);
        Assert.Equal(3, returned[0].TotalSignals);
    }

    [Fact]
    public async Task GetHistory_DaysLessThanOne_ReturnsBadRequest()
    {
        var controller = new MetricsController(new StubAggregator(new PerformanceMetrics(), new List<OutcomeSummary>()));

        var result = await controller.GetHistory(days: 0, cancellationToken: CancellationToken.None);

        Assert.IsType<BadRequestObjectResult>(result.Result);
    }

    private sealed class StubAggregator : IPerformanceMetricsAggregator
    {
        private readonly PerformanceMetrics _metrics;
        private readonly List<OutcomeSummary> _summaries;

        public StubAggregator(PerformanceMetrics metrics, List<OutcomeSummary> summaries)
        {
            _metrics = metrics;
            _summaries = summaries;
        }

        public Task<PerformanceMetrics> GetTodayMetrics(CancellationToken cancellationToken = default)
            => Task.FromResult(_metrics);

        public Task<List<OutcomeSummary>> GetHistoricalSummaries(int days, CancellationToken cancellationToken = default)
            => Task.FromResult(_summaries);
    }
}
