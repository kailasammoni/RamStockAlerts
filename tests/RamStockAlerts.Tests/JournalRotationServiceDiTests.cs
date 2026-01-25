using Microsoft.Extensions.DependencyInjection;
using RamStockAlerts.Services;
using Xunit;

namespace RamStockAlerts.Tests;

/// <summary>
/// Integration test for Phase 2.3: Journal Rotation Service DI registration.
/// </summary>
public class JournalRotationServiceDiTests
{
    [Fact]
    public void JournalRotationService_RegisteredInDi_CanResolve()
    {
        // Arrange
        var services = new ServiceCollection();
        services.AddSingleton<IJournalRotationService, FileBasedJournalRotationService>();

        var sp = services.BuildServiceProvider();

        // Act
        var service = sp.GetRequiredService<IJournalRotationService>();

        // Assert
        Assert.NotNull(service);
        Assert.IsType<FileBasedJournalRotationService>(service);
    }

    [Fact]
    public void JournalRotationService_IsSingleton_ReturnsSameInstance()
    {
        // Arrange
        var services = new ServiceCollection();
        services.AddSingleton<IJournalRotationService, FileBasedJournalRotationService>();

        var sp = services.BuildServiceProvider();

        // Act
        var service1 = sp.GetRequiredService<IJournalRotationService>();
        var service2 = sp.GetRequiredService<IJournalRotationService>();

        // Assert
        Assert.Same(service1, service2);
    }
}
