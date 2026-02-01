using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace RamStockAlerts.Services.Universe;

/// <summary>
/// Service for tracking and computing relative volume metrics.
/// Maintains average daily volume for symbols and computes relative volume (current vs average).
/// </summary>
public class RelativeVolumeService
{
    private readonly ILogger<RelativeVolumeService> _logger;
    private readonly ConcurrentDictionary<string, AverageVolumeData> _avgVolumes = new();

    private sealed record AverageVolumeData(decimal AverageDailyVolume, long LoadedAtUtcMs);

    public RelativeVolumeService(ILogger<RelativeVolumeService> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Sets the average daily volume for a symbol.
    /// </summary>
    public void SetAverageVolume(string symbol, decimal avgVolume, long timestampUtcMs)
    {
        if (string.IsNullOrWhiteSpace(symbol) || avgVolume <= 0)
        {
            return;
        }

        _avgVolumes[symbol] = new AverageVolumeData(avgVolume, timestampUtcMs);
        _logger.LogDebug("[RelVol] Set average volume for {Symbol}: {AvgVolume:N0}", symbol, avgVolume);
    }

    /// <summary>
    /// Gets the average daily volume for a symbol, or null if not available.
    /// </summary>
    public decimal? GetAverageVolume(string symbol)
    {
        if (_avgVolumes.TryGetValue(symbol, out var data))
        {
            return data.AverageDailyVolume;
        }

        return null;
    }

    /// <summary>
    /// Computes relative volume: intradayVolume / averageDailyVolume.
    /// Returns null if average volume is not available for the symbol.
    /// </summary>
    public decimal? GetRelativeVolume(string symbol, decimal intradayVolume)
    {
        var avgVolume = GetAverageVolume(symbol);
        if (avgVolume == null || avgVolume.Value == 0)
        {
            return null;
        }

        return intradayVolume / avgVolume.Value;
    }

    /// <summary>
    /// Checks if a symbol's intraday volume meets the minimum relative volume threshold.
    /// </summary>
    public bool MeetsThreshold(string symbol, decimal intradayVolume, decimal minRelVol = 2.0m)
    {
        var relVol = GetRelativeVolume(symbol, intradayVolume);
        if (relVol == null)
        {
            _logger.LogDebug("[RelVol] {Symbol} has no average volume data, cannot check threshold", symbol);
            return false;
        }

        var meets = relVol.Value >= minRelVol;
        if (!meets)
        {
            _logger.LogDebug(
                "[RelVol] {Symbol} rejected: RelVol={RelVol:F2} < {MinRelVol:F2}",
                symbol,
                relVol.Value,
                minRelVol);
        }

        return meets;
    }

    /// <summary>
    /// Loads average volumes for multiple symbols.
    /// </summary>
    public void LoadAverageVolumes(IEnumerable<KeyValuePair<string, decimal>> symbolVolumes, long timestampUtcMs)
    {
        foreach (var kvp in symbolVolumes)
        {
            SetAverageVolume(kvp.Key, kvp.Value, timestampUtcMs);
        }

        _logger.LogInformation("[RelVol] Loaded average volumes for {Count} symbols", symbolVolumes.Count());
    }

    /// <summary>
    /// Clears all average volume data.
    /// </summary>
    public void Clear()
    {
        _avgVolumes.Clear();
        _logger.LogInformation("[RelVol] Cleared all average volume data");
    }

    /// <summary>
    /// Gets the number of symbols with average volume data.
    /// </summary>
    public int GetLoadedSymbolCount()
    {
        return _avgVolumes.Count;
    }

    /// <summary>
    /// Removes stale average volume data older than the specified TTL.
    /// </summary>
    public void RemoveStaleData(long currentUtcMs, long ttlMs = 24 * 60 * 60 * 1000)
    {
        var staleThreshold = currentUtcMs - ttlMs;
        var staleSymbols = _avgVolumes
            .Where(kvp => kvp.Value.LoadedAtUtcMs < staleThreshold)
            .Select(kvp => kvp.Key)
            .ToList();

        foreach (var symbol in staleSymbols)
        {
            _avgVolumes.TryRemove(symbol, out _);
        }

        if (staleSymbols.Count > 0)
        {
            _logger.LogInformation("[RelVol] Removed {Count} stale average volume entries", staleSymbols.Count);
        }
    }
}
