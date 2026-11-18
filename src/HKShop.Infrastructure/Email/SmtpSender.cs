using System.Net;
using System.Net.Mail;

namespace HKShop.Infrastructure.Email;

public sealed class SmtpSettings
{
    public string Host { get; set; } = "localhost";
    public int Port { get; set; } = 1025;
    public string From { get; set; } = "hkshop@example.test";
    public bool UseTls { get; set; }
    public string User { get; set; } = "";
    public string Password { get; set; } = "";
}

public sealed class SmtpSender(SmtpSettings settings)
{
    public async Task Send(string recipient, string subject, string body, CancellationToken cancellationToken)
    {
        using var client = new SmtpClient(settings.Host, settings.Port) { EnableSsl = settings.UseTls };
        if (settings.User.Length > 0) client.Credentials = new NetworkCredential(settings.User, settings.Password);
        using var message = new MailMessage(settings.From, recipient, subject, body);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
        await client.SendMailAsync(message, timeout.Token);
    }
}
