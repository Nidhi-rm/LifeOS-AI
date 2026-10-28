using System.Net;
using System.Net.Mail;

namespace LifeOS.Api.Services;

public sealed class PasswordResetEmailSender(IConfiguration configuration)
{
    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(configuration["Email:SmtpHost"]) &&
        !string.IsNullOrWhiteSpace(configuration["Email:FromAddress"]);

    public async Task SendCodeAsync(string email, string code, CancellationToken cancellationToken)
    {
        var host = configuration["Email:SmtpHost"];
        var fromAddress = configuration["Email:FromAddress"];
        if (string.IsNullOrWhiteSpace(host) || string.IsNullOrWhiteSpace(fromAddress))
            throw new InvalidOperationException("Email SMTP settings are not configured.");

        var port = int.TryParse(configuration["Email:SmtpPort"], out var configuredPort) ? configuredPort : 587;
        using var message = new MailMessage
        {
            From = new MailAddress(fromAddress, configuration["Email:FromName"] ?? "LifeOS"),
            Subject = "Your LifeOS password reset code",
            Body = $"Your LifeOS password reset code is {code}. It expires in 15 minutes. If you did not request this, you can ignore this email.",
            IsBodyHtml = false
        };
        message.To.Add(new MailAddress(email));

        using var client = new SmtpClient(host, port)
        {
            EnableSsl = !bool.TryParse(configuration["Email:EnableSsl"], out var enableSsl) || enableSsl,
            UseDefaultCredentials = false
        };

        var username = configuration["Email:SmtpUser"];
        var password = configuration["Email:SmtpPassword"];
        if (!string.IsNullOrWhiteSpace(username))
            client.Credentials = new NetworkCredential(username, password ?? string.Empty);

        await client.SendMailAsync(message, cancellationToken);
    }

    public async Task SendTaskReminderAsync(string email, string title, DateTime? dueAt, CancellationToken cancellationToken)
    {
        var host = configuration["Email:SmtpHost"];
        var fromAddress = configuration["Email:FromAddress"];
        if (string.IsNullOrWhiteSpace(host) || string.IsNullOrWhiteSpace(fromAddress))
            throw new InvalidOperationException("Email SMTP settings are not configured.");

        var port = int.TryParse(configuration["Email:SmtpPort"], out var configuredPort) ? configuredPort : 587;
        using var message = new MailMessage
        {
            From = new MailAddress(fromAddress, configuration["Email:FromName"] ?? "LifeOS"),
            Subject = "LifeOS task reminder",
            Body = dueAt is null ? $"Reminder: {title}" : $"Reminder: {title}\nDue {dueAt.Value.ToLocalTime():f}.",
            IsBodyHtml = false
        };
        message.To.Add(new MailAddress(email));
        using var client = new SmtpClient(host, port)
        {
            EnableSsl = !bool.TryParse(configuration["Email:EnableSsl"], out var enableSsl) || enableSsl,
            UseDefaultCredentials = false
        };
        var username = configuration["Email:SmtpUser"];
        if (!string.IsNullOrWhiteSpace(username)) client.Credentials = new NetworkCredential(username, configuration["Email:SmtpPassword"] ?? string.Empty);
        await client.SendMailAsync(message, cancellationToken);
    }
}
