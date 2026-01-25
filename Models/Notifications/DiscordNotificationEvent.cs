namespace RamStockAlerts.Models.Notifications;

public enum DiscordNotificationEventType
{
    Alert,
    Execution,
    Test
}

public enum DiscordNotificationMode
{
    Signals,
    Live,
    Preview
}

public sealed record DiscordNotificationEvent
{
    public Guid EventId { get; init; } = Guid.NewGuid();
    public required DiscordNotificationEventType EventType { get; init; }
    public required string Symbol { get; init; }
    public required DiscordNotificationMode Mode { get; init; }
    public required DateTimeOffset TimestampUtc { get; init; }
    public string? AlertType { get; init; }
    public string? IntendedAction { get; init; }
    public string? Outcome { get; init; }
    public IReadOnlyDictionary<string, string>? Details { get; init; }
}
