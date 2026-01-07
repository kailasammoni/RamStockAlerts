using MailKit.Net.Smtp;
using MailKit.Security;
using MimeKit;
using RamStockAlerts.Models;

namespace RamStockAlerts.Services;

/// <summary>
/// Email alert channel using MailKit SMTP.
/// </summary>
public class EmailAlertChannel : IAlertChannel
{
    private readonly IConfiguration _configuration;
    private readonly ILogger<EmailAlertChannel> _logger;
    private readonly string _smtpHost;
    private readonly int _smtpPort;
    private readonly string _username;
    private readonly string _password;
    private readonly string _fromEmail;
    private readonly string _toEmail;

    public string ChannelName => "Email";

    public EmailAlertChannel(IConfiguration configuration, ILogger<EmailAlertChannel> logger)
    {
        _configuration = configuration;
        _logger = logger;
        _smtpHost = configuration["Email:SmtpHost"] ?? "smtp.gmail.com";
        _smtpPort = configuration.GetValue("Email:SmtpPort", 587);
        _username = configuration["Email:Username"] ?? "";
        _password = configuration["Email:Password"] ?? "";
        _fromEmail = configuration["Email:FromEmail"] ?? "";
        _toEmail = configuration["Email:ToEmail"] ?? "";
    }

    public async Task<bool> SendAsync(TradeSignal signal, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(_username) || string.IsNullOrEmpty(_password))
        {
            _logger.LogWarning("Email credentials not configured");
            return false;
        }

        try
        {
            var message = new MimeMessage();
            message.From.Add(new MailboxAddress("RamStockAlerts", _fromEmail));
            message.To.Add(new MailboxAddress("", _toEmail));
            message.Subject = $"🚨 Trade Alert: {signal.Ticker}";

            message.Body = new TextPart("html")
            {
                Text = $@"
                    <h2>Liquidity Setup Detected</h2>
                    <p><strong>Ticker:</strong> {signal.Ticker}</p>
                    <p><strong>Entry:</strong> ${signal.Entry:F2}</p>
                    <p><strong>Stop:</strong> ${signal.Stop:F2}</p>
                    <p><strong>Target:</strong> ${signal.Target:F2}</p>
                    <p><strong>Score:</strong> {signal.Score:F1}/10</p>
                    <p><strong>Timestamp:</strong> {signal.Timestamp:yyyy-MM-dd HH:mm:ss UTC}</p>
                    <hr>
                    <p style='color: gray; font-size: 12px;'>RamStockAlerts • Liquidity Engine</p>
                "
            };

            using var client = new SmtpClient();
            await client.ConnectAsync(_smtpHost, _smtpPort, SecureSocketOptions.StartTls, cancellationToken);
            await client.AuthenticateAsync(_username, _password, cancellationToken);
            await client.SendAsync(message, cancellationToken);
            await client.DisconnectAsync(true, cancellationToken);

            _logger.LogInformation("Email alert sent for {Ticker}", signal.Ticker);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Email alert failed for {Ticker}", signal.Ticker);
            return false;
        }
    }
}
