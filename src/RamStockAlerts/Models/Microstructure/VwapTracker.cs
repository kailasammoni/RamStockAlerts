using System;

namespace RamStockAlerts.Models.Microstructure;

/// <summary>
/// Lightweight cumulative VWAP tracker (observational only).
/// </summary>
public sealed class VwapTracker
{
    private decimal _cumPv;
    private decimal _cumVolume;

    public decimal CurrentVwap => _cumVolume > 0m ? _cumPv / _cumVolume : 0m;

    public decimal CumVolume => _cumVolume;

    public void OnTrade(double price, decimal size, long timestampMs)
    {
        if (size <= 0m || price <= 0)
        {
            return;
        }

        _cumVolume += size;
        _cumPv += (decimal)price * size;
    }
}
