using RamStockAlerts.Models;
using Twilio;
using Twilio.Rest.Api.V2010.Account;
using Twilio.Types;

namespace RamStockAlerts.Services;

/// <summary>
/// SMS alert channel using Twilio.
/// </summary>
public class SmsAlertChannel : IAlertChannel
{
    private readonly IConfiguration _configuration;
    private readonly ILogger<SmsAlertChannel> _logger;
    private readonly string _accountSid;
    private readonly string _authToken;
    private readonly string _fromNumber;
    private readonly string _toNumber;

    public string ChannelName => "SMS";

    public SmsAlertChannel(IConfiguration configuration, ILogger<SmsAlertChannel> logger)
    {
        _configuration = configuration;
        _logger = logger;
        _accountSid = configuration["Twilio:AccountSid"] ?? "";
        _authToken = configuration["Twilio:AuthToken"] ?? "";
        _fromNumber = configuration["Twilio:FromPhoneNumber"] ?? "";
        _toNumber = configuration["Twilio:ToPhoneNumber"] ?? "";
    }

    public async Task<bool> SendAsync(TradeSignal signal, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(_accountSid) || string.IsNullOrEmpty(_authToken))
        {
            _logger.LogWarning("Twilio credentials not configured");
            return false;
        }

        try
        {
            TwilioClient.Init(_accountSid, _authToken);

            var message = $"🚨 Trade Alert: {signal.Ticker}\n" +
                         $"Entry: ${signal.Entry:F2}\n" +
                         $"Stop: ${signal.Stop:F2}\n" +
                         $"Target: ${signal.Target:F2}\n" +
                         $"Score: {signal.Score:F1}/10";

            var messageResource = await MessageResource.CreateAsync(
                body: message,
                from: new PhoneNumber(_fromNumber),
                to: new PhoneNumber(_toNumber)
            );

            _logger.LogInformation("SMS alert sent for {Ticker}, SID: {Sid}", signal.Ticker, messageResource.Sid);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "SMS alert failed for {Ticker}", signal.Ticker);
            return false;
        }
    }
}
