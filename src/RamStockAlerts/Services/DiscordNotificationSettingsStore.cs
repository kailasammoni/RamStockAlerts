using Microsoft.Extensions.Configuration;
using RamStockAlerts.Models.Notifications;

namespace RamStockAlerts.Services;

public sealed class DiscordNotificationSettingsStore
{
    private readonly IConfiguration _configuration;
    private readonly object _lock = new();
    private DiscordNotificationSettings? _overrideSettings;

    public DiscordNotificationSettingsStore(IConfiguration configuration)
    {
        _configuration = configuration;
    }

    public DiscordNotificationSettings GetSettings()
    {
        lock (_lock)
        {
            if (_overrideSettings != null)
            {
                return _overrideSettings;
            }
        }

        return ReadFromConfiguration();
    }

    public DiscordNotificationSettings UpdateSettings(DiscordNotificationSettings settings)
    {
        lock (_lock)
        {
            _overrideSettings = settings;
            return settings;
        }
    }

    private DiscordNotificationSettings ReadFromConfiguration()
    {
        return new DiscordNotificationSettings
        {
            Enabled = _configuration.GetValue("Discord:Enabled", false),
            WebhookUrl = _configuration["Discord:WebhookUrl"],
            ChannelTag = _configuration.GetValue<string?>("Discord:ChannelTag"),
            IncludeModeTag = _configuration.GetValue("Discord:IncludeModeTag", true)
        };
    }
}
