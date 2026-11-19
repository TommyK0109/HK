using HKShop.Infrastructure.Email;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace HKShop.Notification;

public sealed class ConfirmationEmailSender(IDbContextFactory<NotificationDbContext> database, SmtpSender smtp,
    ILogger<ConfirmationEmailSender> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            try
            {
                await using var db = await database.CreateDbContextAsync(token);
                var now = DateTimeOffset.UtcNow;
                var pending = db.ConfirmationEmails.Where(e => e.SentAt == null && e.Attempts < 5 && e.NextAttemptAt <= now);
                var batch = await pending.AsNoTracking().OrderBy(e => e.NextAttemptAt).Take(15).ToListAsync(token);
                foreach (var email in batch)
                {
                    // Lease exceeds the SMTP client's 15-second timeout and expires if this process dies.
                    var lease = DateTimeOffset.UtcNow.AddMinutes(1);
                    var claimed = await pending.Where(e => e.OrderId == email.OrderId)
                        .ExecuteUpdateAsync(set => set.SetProperty(e => e.NextAttemptAt, lease), token);
                    if (claimed == 0) continue;
                    var owned = db.ConfirmationEmails.Where(e => e.OrderId == email.OrderId && e.NextAttemptAt == lease);
                    try
                    {
                        await smtp.Send(email.Recipient, "Order confirmed", $"Order confirmed\nOrder: {email.OrderNumber}", token);
                    }
                    catch (Exception error) when (!token.IsCancellationRequested)
                    {
                        var attempts = email.Attempts + 1;
                        var retryAt = DateTimeOffset.UtcNow.AddMinutes(Math.Pow(2, attempts));
                        await owned.ExecuteUpdateAsync(set => set.SetProperty(e => e.Attempts, attempts)
                            .SetProperty(e => e.NextAttemptAt, retryAt), token);
                        logger.LogWarning("Confirmation SMTP attempt {Attempt}/5 failed for order {OrderId} ({ErrorType}).",
                            attempts, email.OrderId, error.GetType().Name);
                        continue;
                    }
                    await owned.ExecuteUpdateAsync(set => set.SetProperty(e => e.SentAt, DateTimeOffset.UtcNow)
                        .SetProperty(e => e.Attempts, e => e.Attempts + 1), token);
                }
            }
            catch (Exception error) when (!token.IsCancellationRequested)
            {
                logger.LogWarning("Notification email processing will retry after {ErrorType}.", error.GetType().Name);
            }
            await Task.Delay(TimeSpan.FromSeconds(2), token);
        }
    }
}
