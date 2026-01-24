namespace RamStockAlerts.Models.Notifications;

public sealed class DiscordDeliveryStatus
{
    public required string DestinationKey { get; init; }
    public DateTimeOffset LastAttemptAt { get; set; }
    public DateTimeOffset? LastSuccessAt { get; set; }
    public DateTimeOffset? LastFailureAt { get; set; }
    public int? LastStatusCode { get; set; }
    public string? LastError { get; set; }
}
