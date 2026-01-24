namespace RamStockAlerts.Controllers.Api.Admin;

public sealed record DiscordNotificationSettingsRequest
{
    public bool Enabled { get; init; }
    public string WebhookUrl { get; init; } = string.Empty;
    public string? ChannelTag { get; init; }
}

public sealed record DiscordNotificationSettingsDto
{
    public required bool Enabled { get; init; }
    public string? WebhookUrlMasked { get; init; }
    public string? ChannelTag { get; init; }
}

public sealed record DiscordDeliveryStatusDto
{
    public DateTimeOffset? LastAttemptAt { get; init; }
    public DateTimeOffset? LastSuccessAt { get; init; }
    public DateTimeOffset? LastFailureAt { get; init; }
    public int? LastStatusCode { get; init; }
    public string? LastError { get; init; }
}

public sealed record DiscordNotificationStatusResponse
{
    public required DiscordNotificationSettingsDto Settings { get; init; }
    public DiscordDeliveryStatusDto? Status { get; init; }
}

public sealed record DiscordTestRequest
{
    public string? Message { get; init; }
}

public sealed record DiscordTestResponse
{
    public required bool Success { get; init; }
    public DiscordDeliveryStatusDto? Status { get; init; }
}
