namespace RamStockAlerts.Models.Notifications;

public sealed record DiscordNotificationSettings
{
    public required bool Enabled { get; init; }
    public required string? WebhookUrl { get; init; }
    public string? ChannelTag { get; init; }
    public bool IncludeModeTag { get; init; } = true;
    public bool CompactAlertFields { get; init; }
}
