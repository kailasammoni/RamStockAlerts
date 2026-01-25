using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace RamStockAlerts.Universe;

public sealed class StaticUniverseSource : IUniverseSource
{
    private readonly IConfiguration _configuration;
    private readonly ILogger<StaticUniverseSource> _logger;

    public StaticUniverseSource(IConfiguration configuration, ILogger<StaticUniverseSource> logger)
    {
        _configuration = configuration;
        _logger = logger;
    }

    public Task<IReadOnlyList<string>> GetUniverseAsync(CancellationToken cancellationToken)
    {
        var tickers = _configuration.GetSection("Universe:StaticTickers").Get<string[]>()
            ?? Array.Empty<string>();

        var normalized = tickers
            .Where(t => !string.IsNullOrWhiteSpace(t))
            .Select(t => t.Trim().ToUpperInvariant())
            .Distinct()
            .ToList();

        if (normalized.Count == 0)
        {
            _logger.LogWarning("Static universe is empty. Configure Universe:StaticTickers.");
        }

        return Task.FromResult<IReadOnlyList<string>>(normalized);
    }
}
