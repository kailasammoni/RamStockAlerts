using RamStockAlerts.Models;

namespace RamStockAlerts.Services;

/// <summary>
/// Multi-channel notification service with failover support.
/// Tries channels in order: Discord -> SMS -> Email.
/// </summary>
public class MultiChannelNotificationService
{
    private readonly IEnumerable<IAlertChannel> _channels;
    private readonly ILogger<MultiChannelNotificationService> _logger;

    public MultiChannelNotificationService(
        IEnumerable<IAlertChannel> channels,
        ILogger<MultiChannelNotificationService> logger)
    {
        _channels = channels;
        _logger = logger;
    }

    public async Task<bool> SendWithFailoverAsync(TradeSignal signal, CancellationToken cancellationToken = default)
    {
        foreach (var channel in _channels)
        {
            try
            {
                var success = await channel.SendAsync(signal, cancellationToken);
                if (success)
                {
                    _logger.LogInformation("Alert sent successfully via {Channel} for {Ticker}", 
                        channel.ChannelName, signal.Ticker);
                    return true;
                }
                
                _logger.LogWarning("Alert failed via {Channel}, trying next channel", channel.ChannelName);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error sending alert via {Channel}", channel.ChannelName);
            }
        }

        _logger.LogError("All alert channels failed for {Ticker}", signal.Ticker);
        return false;
    }
}
