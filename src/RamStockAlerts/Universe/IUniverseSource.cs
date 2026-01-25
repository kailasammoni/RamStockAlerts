namespace RamStockAlerts.Universe;

public interface IUniverseSource
{
    Task<IReadOnlyList<string>> GetUniverseAsync(CancellationToken cancellationToken);
}
