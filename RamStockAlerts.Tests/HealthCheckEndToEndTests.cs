using System.Net;

namespace RamStockAlerts.Tests;

/// <summary>
/// End-to-end integration tests for health check endpoints.
/// </summary>
public class HealthCheckEndToEndTests : IDisposable
{
    private readonly TestWebApplicationFactory _factory;
    private readonly HttpClient _client;

    public HealthCheckEndToEndTests()
    {
        _factory = new TestWebApplicationFactory();
        _client = _factory.CreateClient();
    }

    public void Dispose()
    {
        _client?.Dispose();
        _factory?.Dispose();
    }

    [Fact]
    public async Task WhenCallingLivenessEndpointThenReturnsHealthy()
    {
        // Act - Call the liveness health check
        var response = await _client.GetAsync("/health/live");

        // Assert - Liveness check returns success
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        
        var content = await response.Content.ReadAsStringAsync();
        Assert.Contains("Healthy", content);
    }

    [Fact]
    public async Task WhenCallingReadinessEndpointThenReturnsHealthy()
    {
        // Act - Call the readiness health check
        var response = await _client.GetAsync("/health/ready");

        // Assert - Readiness check returns success
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        
        var content = await response.Content.ReadAsStringAsync();
        Assert.Contains("Healthy", content);
    }
}
