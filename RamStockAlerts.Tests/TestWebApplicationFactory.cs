using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using RamStockAlerts.Data;

namespace RamStockAlerts.Tests;

/// <summary>
/// Custom WebApplicationFactory for integration testing.
/// Uses in-memory database to isolate tests.
/// </summary>
public class TestWebApplicationFactory : WebApplicationFactory<Program>
{
    private readonly string _databaseName = $"TestDatabase-{Guid.NewGuid()}";

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        
        builder.ConfigureTestServices(services =>
        {
            // Remove all EF Core and DbContext related registrations
            var toRemove = services
                .Where(d => d.ServiceType.Namespace != null && 
                    (d.ServiceType.Namespace.StartsWith("Microsoft.EntityFrameworkCore") ||
                     d.ServiceType == typeof(AppDbContext) ||
                     d.ServiceType == typeof(DbContextOptions<AppDbContext>)))
                .ToList();
            
            foreach (var descriptor in toRemove)
            {
                services.Remove(descriptor);
            }

            // Add fresh AppDbContext using InMemory database for testing
            // Use the same database name for all requests in this test
            services.AddDbContext<AppDbContext>(options =>
            {
                options.UseInMemoryDatabase(_databaseName);
            });
        });
    }
}
