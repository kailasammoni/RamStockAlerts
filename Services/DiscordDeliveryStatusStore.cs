using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using RamStockAlerts.Models.Notifications;

namespace RamStockAlerts.Services;

public sealed class DiscordDeliveryStatusStore
{
    private readonly ConcurrentDictionary<string, DiscordDeliveryStatus> _statuses = new(StringComparer.OrdinalIgnoreCase);

    public DiscordDeliveryStatus? GetStatus(string destinationKey)
    {
        return _statuses.TryGetValue(destinationKey, out var status) ? status : null;
    }

    public DiscordDeliveryStatus? GetStatusForWebhook(string? webhookUrl)
    {
        var destinationKey = GetDestinationKey(webhookUrl);
        return destinationKey == null ? null : GetStatus(destinationKey);
    }

    public DiscordDeliveryStatus UpdateStatus(string destinationKey, bool success, int? statusCode, string? error)
    {
        var now = DateTimeOffset.UtcNow;

        return _statuses.AddOrUpdate(
            destinationKey,
            _ => new DiscordDeliveryStatus
            {
                DestinationKey = destinationKey,
                LastAttemptAt = now,
                LastSuccessAt = success ? now : null,
                LastFailureAt = success ? null : now,
                LastStatusCode = statusCode,
                LastError = success ? null : error
            },
            (_, existing) =>
            {
                existing.LastAttemptAt = now;
                if (success)
                {
                    existing.LastSuccessAt = now;
                    existing.LastError = null;
                }
                else
                {
                    existing.LastFailureAt = now;
                    existing.LastError = error;
                }

                existing.LastStatusCode = statusCode;
                return existing;
            });
    }

    public static string? GetDestinationKey(string? webhookUrl)
    {
        if (string.IsNullOrWhiteSpace(webhookUrl))
        {
            return null;
        }

        var normalized = webhookUrl.Trim();
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(normalized));
        var hex = Convert.ToHexString(hash).ToLowerInvariant();
        return hex[..12];
    }
}
