using System.Text.Json;
using HKShop.Contracts.Events;
using HKShop.Infrastructure.Email;
using HKShop.Messaging;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace HKShop.Notification;

public sealed class NotificationInboxHandler : IIntegrationEventHandler<NotificationDbContext>
{
    public async Task HandleAsync(NotificationDbContext db, EventEnvelope envelope, CancellationToken ct)
    {
        NotificationMessage? msg = envelope.Type switch
        {
            nameof(UserRegistered) => FromUserRegistered(envelope),
            nameof(EmailConfirmationRequested) => FromEmailConfirmation(envelope),
            nameof(PasswordResetRequested) => FromPasswordReset(envelope),
            nameof(SellerInvited) => FromSellerInvited(envelope),
            nameof(OrderCancelled) => FromOrderCancelled(envelope),
            nameof(SellerOrderDispatched) => FromDispatched(envelope),
            nameof(SellerOrderDelivered) => FromDelivered(envelope),
            nameof(PaymentFailed) => FromPaymentFailed(envelope),
            nameof(PaymentExpired) => FromPaymentExpired(envelope),
            nameof(RefundCompletedEvent) => FromRefundCompleted(envelope),
            _ => null
        };

        if (msg is null || string.IsNullOrWhiteSpace(msg.Recipient))
            return;

        if (!await db.NotificationMessages.AnyAsync(m => m.EventKey == msg.EventKey, ct))
            db.NotificationMessages.Add(msg);
    }

    private static NotificationMessage? FromUserRegistered(EventEnvelope env)
    {
        var e = env.Payload.Deserialize<UserRegistered>();
        return e is null ? null : Create($"user-registered:{e.UserId}", e.Email, "Confirm your HKShop email", e.ConfirmationUrl);
    }

    private static NotificationMessage? FromEmailConfirmation(EventEnvelope env)
    {
        var e = env.Payload.Deserialize<EmailConfirmationRequested>();
        return e is null ? null : Create($"email-confirm:{env.EventId:N}", e.Email, "Confirm your HKShop email", e.ConfirmationUrl);
    }

    private static NotificationMessage? FromPasswordReset(EventEnvelope env)
    {
        var e = env.Payload.Deserialize<PasswordResetRequested>();
        return e is null ? null : Create($"password-reset:{env.EventId:N}", e.Email, "Reset your HKShop password", e.ResetUrl);
    }

    private static NotificationMessage? FromSellerInvited(EventEnvelope env)
    {
        var e = env.Payload.Deserialize<SellerInvited>();
        return e is null ? null : Create($"seller-invitation:{e.InvitationId}", e.Email,
            "Invitation to join an HKShop store", $"You are invited to join {e.StoreName}:\n{e.AcceptUrl}");
    }

    private static NotificationMessage? FromOrderCancelled(EventEnvelope env)
    {
        var e = env.Payload.Deserialize<OrderCancelled>();
        return e is null ? null : Create($"order-cancelled:{e.OrderId:N}", e.CustomerEmail,
            "Order cancelled", $"Order cancelled\nOrder: {e.OrderNumber}");
    }

    private static NotificationMessage? FromDispatched(EventEnvelope env)
    {
        var e = env.Payload.Deserialize<SellerOrderDispatched>();
        return e is null ? null : Create($"shipped:{e.SellerOrderId:N}", e.CustomerEmail,
            $"Seller order {e.SellerOrderNumber} shipped", $"Order shipped ({e.Carrier} {e.TrackingNumber})");
    }

    private static NotificationMessage? FromDelivered(EventEnvelope env)
    {
        var e = env.Payload.Deserialize<SellerOrderDelivered>();
        return e is null ? null : Create($"delivered:{e.SellerOrderId:N}", e.CustomerEmail,
            $"Seller order {e.SellerOrderNumber} delivered", $"Seller order {e.SellerOrderNumber} delivered");
    }

    private static NotificationMessage? FromPaymentFailed(EventEnvelope env)
    {
        var e = env.Payload.Deserialize<PaymentFailed>();
        return e is null ? null : Create($"payment-failed:{e.OrderId:N}", e.CustomerEmail,
            "Payment failed", $"Payment failed\nOrder: {e.OrderNumber}");
    }

    private static NotificationMessage? FromPaymentExpired(EventEnvelope env)
    {
        var e = env.Payload.Deserialize<PaymentExpired>();
        return e is null ? null : Create($"expired:{e.OrderId:N}", e.CustomerEmail,
            "Order payment expired", $"Order payment expired\nOrder: {e.OrderNumber}");
    }

    private static NotificationMessage? FromRefundCompleted(EventEnvelope env)
    {
        var e = env.Payload.Deserialize<RefundCompletedEvent>();
        return e is null ? null : Create($"refund:{e.RefundId}", e.CustomerEmail,
            e.Simulated ? "Simulated refund completed" : "Refund completed",
            $"Refund completed for order {e.OrderNumber}: {e.AmountVnd:N0} VND ({e.Reference})");
    }

    private static NotificationMessage Create(string key, string recipient, string subject, string body) => new()
    {
        EventKey = key.Length > 160 ? key[..160] : key,
        Recipient = recipient.Length > 256 ? recipient[..256] : recipient,
        Subject = subject.Length > 200 ? subject[..200] : subject,
        Body = body.Length > 2000 ? body[..2000] : body,
        NextAttemptAt = DateTimeOffset.UtcNow
    };
}

public sealed class NotificationMessageSender(
    IDbContextFactory<NotificationDbContext> database,
    SmtpSender smtp,
    ILogger<NotificationMessageSender> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            try
            {
                await using var db = await database.CreateDbContextAsync(token);
                var now = DateTimeOffset.UtcNow;
                var pending = db.NotificationMessages.Where(m => m.SentAt == null && m.Attempts < 5 && m.NextAttemptAt <= now);
                var batch = await pending.AsNoTracking().OrderBy(m => m.NextAttemptAt).Take(15).ToListAsync(token);

                foreach (var msg in batch)
                {
                    var lease = DateTimeOffset.UtcNow.AddMinutes(1);
                    var claimed = await pending.Where(m => m.Id == msg.Id)
                        .ExecuteUpdateAsync(s => s.SetProperty(m => m.NextAttemptAt, lease), token);
                    if (claimed == 0) continue;

                    var owned = db.NotificationMessages.Where(m => m.Id == msg.Id && m.NextAttemptAt == lease);
                    try
                    {
                        await smtp.Send(msg.Recipient, msg.Subject, msg.Body, token);
                    }
                    catch (Exception error) when (!token.IsCancellationRequested)
                    {
                        var attempts = msg.Attempts + 1;
                        var retryAt = DateTimeOffset.UtcNow.AddMinutes(Math.Pow(2, attempts));
                        await owned.ExecuteUpdateAsync(s => s.SetProperty(m => m.Attempts, attempts)
                            .SetProperty(m => m.NextAttemptAt, retryAt), token);
                        logger.LogWarning("Notification SMTP attempt {Attempt}/5 failed for message {Id} ({ErrorType}).",
                            attempts, msg.Id, error.GetType().Name);
                        continue;
                    }

                    await owned.ExecuteUpdateAsync(s => s.SetProperty(m => m.SentAt, DateTimeOffset.UtcNow)
                        .SetProperty(m => m.Attempts, m => m.Attempts + 1), token);
                }
            }
            catch (Exception error) when (!token.IsCancellationRequested)
            {
                logger.LogWarning("Notification message delivery loop will retry after {ErrorType}.", error.GetType().Name);
            }
            await Task.Delay(TimeSpan.FromSeconds(2), token);
        }
    }
}
