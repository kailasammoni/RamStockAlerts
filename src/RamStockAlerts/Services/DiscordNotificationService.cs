using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using RamStockAlerts.Models.Notifications;

namespace RamStockAlerts.Services;

public sealed record DiscordNotificationSendOptions
{
    public bool? EnabledOverride { get; init; }
    public string? WebhookUrlOverride { get; init; }
    public string? ChannelTagOverride { get; init; }
}

public sealed record DiscordNotificationSendResult
{
    public required bool Success { get; init; }
    public DiscordDeliveryStatus? Status { get; init; }
}

public sealed class DiscordNotificationService
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private readonly DiscordNotificationSettingsStore _settingsStore;
    private readonly DiscordDeliveryStatusStore _statusStore;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<DiscordNotificationService> _logger;

    public DiscordNotificationService(
        DiscordNotificationSettingsStore settingsStore,
        DiscordDeliveryStatusStore statusStore,
        IHttpClientFactory httpClientFactory,
        ILogger<DiscordNotificationService> logger)
    {
        _settingsStore = settingsStore;
        _statusStore = statusStore;
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    public DiscordNotificationEvent BuildAlertEvent(
        string symbol,
        string alertType,
        DateTimeOffset timestampUtc,
        DiscordNotificationMode mode,
        string? intendedAction,
        IReadOnlyDictionary<string, string>? details = null)
    {
        return new DiscordNotificationEvent
        {
            EventType = DiscordNotificationEventType.Alert,
            Symbol = symbol,
            AlertType = alertType,
            TimestampUtc = timestampUtc,
            Mode = mode,
            IntendedAction = intendedAction,
            Details = details
        };
    }

    public DiscordNotificationEvent BuildExecutionEvent(
        string symbol,
        string status,
        DateTimeOffset timestampUtc,
        DiscordNotificationMode mode,
        IReadOnlyDictionary<string, string>? details = null)
    {
        return new DiscordNotificationEvent
        {
            EventType = DiscordNotificationEventType.Execution,
            Symbol = symbol,
            Outcome = status,
            TimestampUtc = timestampUtc,
            Mode = mode,
            Details = details
        };
    }

    public DiscordNotificationEvent BuildTestEvent(string message, DateTimeOffset timestampUtc)
    {
        var details = new Dictionary<string, string>
        {
            ["Message"] = message
        };

        return new DiscordNotificationEvent
        {
            EventType = DiscordNotificationEventType.Test,
            Symbol = "SYSTEM",
            Outcome = "Test",
            TimestampUtc = timestampUtc,
            Mode = DiscordNotificationMode.Preview,
            Details = details
        };
    }

    public Task<DiscordNotificationSendResult> SendAlertAsync(
        string symbol,
        string alertType,
        DateTimeOffset timestampUtc,
        DiscordNotificationMode mode,
        string? intendedAction,
        IReadOnlyDictionary<string, string>? details = null,
        DiscordNotificationSendOptions? options = null,
        CancellationToken ct = default)
    {
        var notification = BuildAlertEvent(symbol, alertType, timestampUtc, mode, intendedAction, details);
        return SendAsync(notification, options, ct);
    }

    public Task<DiscordNotificationSendResult> SendExecutionStatusAsync(
        string symbol,
        string status,
        DateTimeOffset timestampUtc,
        DiscordNotificationMode mode,
        IReadOnlyDictionary<string, string>? details = null,
        CancellationToken ct = default)
    {
        var notification = BuildExecutionEvent(symbol, status, timestampUtc, mode, details);
        return SendAsync(notification, options: null, ct);
    }

    public Task<DiscordNotificationSendResult> SendTestAsync(
        string? message,
        CancellationToken ct = default)
    {
        var payload = string.IsNullOrWhiteSpace(message)
            ? "Discord notification test"
            : message.Trim();
        var notification = BuildTestEvent(payload, DateTimeOffset.UtcNow);
        return SendAsync(notification, options: null, ct);
    }

    private async Task<DiscordNotificationSendResult> SendAsync(
        DiscordNotificationEvent notification,
        DiscordNotificationSendOptions? options,
        CancellationToken ct)
    {
        try
        {
            var baseSettings = _settingsStore.GetSettings();
            var settings = ApplyOverrides(baseSettings, options);

            if (!settings.Enabled)
            {
                _logger.LogInformation("[Discord] Notifications disabled; skipping {EventType} for {Symbol}", notification.EventType, notification.Symbol);
                var status = UpdateStatusIfPossible(settings.WebhookUrl, false, null, "Discord notifications disabled");
                return new DiscordNotificationSendResult { Success = false, Status = status };
            }

            if (!TryGetWebhookUri(settings.WebhookUrl, out var webhookUri))
            {
                _logger.LogWarning("[Discord] Missing or invalid webhook URL; skipping {EventType} for {Symbol}", notification.EventType, notification.Symbol);
                var status = UpdateStatusIfPossible(settings.WebhookUrl, false, null, "Discord webhook URL missing or invalid");
                return new DiscordNotificationSendResult { Success = false, Status = status };
            }

            var embed = BuildEmbed(notification, settings);
            var payload = new { embeds = new[] { embed } };
            var json = JsonSerializer.Serialize(payload, SerializerOptions);
            using var content = new StringContent(json, Encoding.UTF8, "application/json");

            var client = _httpClientFactory.CreateClient();
            using var response = await client.PostAsync(webhookUri, content, ct);

            if (response.IsSuccessStatusCode)
            {
                var status = UpdateStatusIfPossible(settings.WebhookUrl, true, (int)response.StatusCode, null);
                return new DiscordNotificationSendResult { Success = true, Status = status };
            }

            var errorContent = await response.Content.ReadAsStringAsync(ct);
            var error = string.IsNullOrWhiteSpace(errorContent)
                ? $"Discord webhook failed with {(int)response.StatusCode} ({response.StatusCode})"
                : Truncate(errorContent.Trim(), 400);

            _logger.LogWarning("[Discord] Webhook failed: {StatusCode} {Error}", response.StatusCode, error);
            var failureStatus = UpdateStatusIfPossible(settings.WebhookUrl, false, (int)response.StatusCode, error);
            return new DiscordNotificationSendResult { Success = false, Status = failureStatus };
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "[Discord] Notification send failed for {EventType} {Symbol}", notification.EventType, notification.Symbol);
            var status = UpdateStatusIfPossible(_settingsStore.GetSettings().WebhookUrl, false, null, ex.Message);
            return new DiscordNotificationSendResult { Success = false, Status = status };
        }
    }

    private static DiscordNotificationSettings ApplyOverrides(
        DiscordNotificationSettings settings,
        DiscordNotificationSendOptions? options)
    {
        if (options == null)
        {
            return settings;
        }

        return settings with
        {
            Enabled = options.EnabledOverride ?? settings.Enabled,
            WebhookUrl = options.WebhookUrlOverride ?? settings.WebhookUrl,
            ChannelTag = options.ChannelTagOverride ?? settings.ChannelTag
        };
    }

    private static object BuildEmbed(DiscordNotificationEvent notification, DiscordNotificationSettings settings)
    {
        var title = BuildTitle(notification, settings);
        var fields = BuildFields(notification, settings);

        return new
        {
            title,
            fields,
            timestamp = notification.TimestampUtc.ToString("o")
        };
    }

    private static string BuildTitle(DiscordNotificationEvent notification, DiscordNotificationSettings settings)
    {
        var tagPrefix = string.IsNullOrWhiteSpace(settings.ChannelTag)
            ? string.Empty
            : $"[{settings.ChannelTag}] ";

        var baseTitle = notification.EventType switch
        {
            DiscordNotificationEventType.Alert => notification.AlertType ?? "Alert",
            DiscordNotificationEventType.Execution => notification.Outcome != null ? $"Execution {notification.Outcome}" : "Execution Update",
            DiscordNotificationEventType.Test => "Discord Test",
            _ => "Notification"
        };

        return $"{tagPrefix}{baseTitle}".Trim();
    }

    private static List<object> BuildFields(DiscordNotificationEvent notification, DiscordNotificationSettings settings)
    {
        var compactAlertFields = settings.CompactAlertFields && notification.EventType == DiscordNotificationEventType.Alert;
        var fields = new List<object>
        {
            new { name = "Symbol", value = notification.Symbol, inline = true }
        };

        if (!string.IsNullOrWhiteSpace(notification.AlertType))
        {
            fields.Add(new { name = "AlertType", value = notification.AlertType, inline = true });
        }

        if (settings.IncludeModeTag && !compactAlertFields)
        {
            fields.Add(new { name = "Mode", value = notification.Mode.ToString(), inline = true });
        }

        if (!string.IsNullOrWhiteSpace(notification.IntendedAction))
        {
            fields.Add(new { name = "IntendedAction", value = notification.IntendedAction, inline = true });
        }

        if (!string.IsNullOrWhiteSpace(notification.Outcome))
        {
            fields.Add(new { name = "Outcome", value = notification.Outcome, inline = true });
        }

        if (!compactAlertFields)
        {
            fields.Add(new { name = "Timestamp", value = notification.TimestampUtc.ToString("u"), inline = true });
        }

        if (notification.Details != null)
        {
            var details = notification.Details;
            if (compactAlertFields)
            {
                var entry = GetDetailValue(details, "Entry");
                var stop = GetDetailValue(details, "Stop");
                var target = GetDetailValue(details, "Target");

                var blueprintValue = BuildBlueprintValue(entry, stop, target);
                if (!string.IsNullOrWhiteSpace(blueprintValue))
                {
                    fields.Add(new { name = "Blueprint", value = blueprintValue, inline = false });
                }
            }

            var hiddenKeys = compactAlertFields
                ? new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                {
                    "Entry",
                    "Stop",
                    "Target",
                    "Spread",
                    "BidAskRatio",
                    "TapeVelocityProxy"
                }
                : null;

            foreach (var detail in details)
            {
                if (!string.IsNullOrWhiteSpace(detail.Key) && !string.IsNullOrWhiteSpace(detail.Value))
                {
                    if (hiddenKeys != null && hiddenKeys.Contains(detail.Key))
                    {
                        continue;
                    }

                    fields.Add(new { name = detail.Key, value = detail.Value, inline = true });
                }
            }
        }

        return fields;
    }

    private static string? GetDetailValue(IReadOnlyDictionary<string, string> details, string key)
    {
        foreach (var detail in details)
        {
            if (string.Equals(detail.Key, key, StringComparison.OrdinalIgnoreCase))
            {
                return detail.Value;
            }
        }

        return null;
    }

    private static string? BuildBlueprintValue(string? entry, string? stop, string? target)
    {
        var segments = new List<string>();
        if (!string.IsNullOrWhiteSpace(entry))
        {
            segments.Add($"Entry {entry}");
        }

        if (!string.IsNullOrWhiteSpace(stop))
        {
            segments.Add($"Stop {stop}");
        }

        if (!string.IsNullOrWhiteSpace(target))
        {
            segments.Add($"Target {target}");
        }

        return segments.Count == 0 ? null : string.Join(" | ", segments);
    }

    private DiscordDeliveryStatus? UpdateStatusIfPossible(string? webhookUrl, bool success, int? statusCode, string? error)
    {
        var destinationKey = DiscordDeliveryStatusStore.GetDestinationKey(webhookUrl);
        return destinationKey == null
            ? null
            : _statusStore.UpdateStatus(destinationKey, success, statusCode, error);
    }

    private static bool TryGetWebhookUri(string? webhookUrl, out Uri webhookUri)
    {
        webhookUri = null!;
        if (string.IsNullOrWhiteSpace(webhookUrl))
        {
            return false;
        }

        if (!Uri.TryCreate(webhookUrl.Trim(), UriKind.Absolute, out var uri))
        {
            return false;
        }

        if (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp)
        {
            return false;
        }

        webhookUri = uri;
        return true;
    }

    private static string Truncate(string value, int maxLength)
    {
        if (string.IsNullOrEmpty(value) || value.Length <= maxLength)
        {
            return value;
        }

        return value[..maxLength];
    }
}
