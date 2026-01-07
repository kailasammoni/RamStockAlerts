using RamStockAlerts.Models;

namespace RamStockAlerts.Services;

/// <summary>
/// Interface for multi-channel alert delivery.
/// </summary>
public interface IAlertChannel
{
    string ChannelName { get; }
    Task<bool> SendAsync(TradeSignal signal, CancellationToken cancellationToken = default);
}
