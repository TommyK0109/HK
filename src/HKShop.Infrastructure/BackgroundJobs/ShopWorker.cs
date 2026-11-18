using HKShop.Application;
using HKShop.Application.Payments;
using HKShop.Application.Settlement;
using HKShop.Application.Transportation;
using HKShop.Domain.Cart;
using HKShop.Domain.Orders;
using HKShop.Infrastructure.Email;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace HKShop.Infrastructure.BackgroundJobs;

public sealed class CommerceMaintenanceWorker(IServiceScopeFactory scopes, TimeProvider time, ILogger<CommerceMaintenanceWorker> logger) : BackgroundService
{
    private DateTimeOffset nextCleanup;
    private DateTimeOffset nextAlert;

    protected override Task ExecuteAsync(CancellationToken stoppingToken) => Task.WhenAll(
        Loop("reservations", async (services, _) =>
        {
            await services.GetRequiredService<PaymentService>().Expire();
            await Maintain(services.GetRequiredService<IShopStore>());
        }, stoppingToken),
        Loop("reconciliation", Reconcile, stoppingToken),
        Loop("refunds", ReconcileRefunds, stoppingToken),
        Loop("settlement", async (services, _) => { await services.GetRequiredService<SettlementLedger>().ReleaseDue(); }, stoppingToken),
        Loop("payouts", ReconcilePayouts, stoppingToken));

    private async Task Loop(string name, Func<IServiceProvider, CancellationToken, Task> work, CancellationToken token)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(30), time);
        while (await timer.WaitForNextTickAsync(token))
        {
            try
            {
                using var scope = scopes.CreateScope();
                await work(scope.ServiceProvider, token);
            }
            catch (Exception error) when (!token.IsCancellationRequested)
            {
                logger.LogWarning("Commerce background task {JobName} failed ({ErrorType}).", name, error.GetType().Name);
            }
        }
    }

    private async Task Reconcile(IServiceProvider services, CancellationToken token)
    {
        var payments = services.GetRequiredService<PaymentService>();
        if (!payments.OnlineAvailable) return;
        foreach (var job in await payments.DueQueries())
        {
            try { await payments.Reconcile(job.OrderId, job.Number, token); }
            catch (Exception error) when (!token.IsCancellationRequested)
            {
                logger.LogWarning("SePay reconciliation failed for {OrderId} ({ErrorType}).", job.OrderId, error.GetType().Name);
            }
        }
    }

    private async Task ReconcileRefunds(IServiceProvider services, CancellationToken token)
    {
        var refunds = services.GetRequiredService<RefundWorkflow>();
        foreach (var id in await refunds.DueRefunds())
        {
            try { await refunds.Reconcile(id, token); }
            catch (Exception error) when (!token.IsCancellationRequested)
            {
                logger.LogWarning("Refund reconciliation failed for {RefundId} ({ErrorType}).", id, error.GetType().Name);
            }
        }
    }

    private async Task ReconcilePayouts(IServiceProvider services, CancellationToken token)
    {
        var payouts = services.GetRequiredService<PayoutService>();
        foreach (var id in await payouts.DuePayouts())
        {
            try { await payouts.Reconcile(id, token); }
            catch (Exception error) when (!token.IsCancellationRequested)
            {
                logger.LogWarning("Payout reconciliation failed for {PayoutId} ({ErrorType}).", id, error.GetType().Name);
            }
        }
    }

    private async Task Maintain(IShopStore store)
    {
        var now = time.GetUtcNow();
        if (now >= nextCleanup)
        {
            await store.Write(async db =>
            {
                var old = await db.List(db.Query<ShoppingCart>().Where(c => c.GuestTokenHash != null &&
                    c.UpdatedAt < now.AddDays(-30)).OrderBy(c => c.UpdatedAt).Take(100));
                foreach (var cart in old) db.Remove(cart);
                return old.Count;
            });
            nextCleanup = now.AddMinutes(10);
        }
        if (now >= nextAlert)
        {
            var refunds = await store.Read(db => db.Count(db.Query<Refund>().Where(r =>
                r.Status == RefundStatus.Pending && r.RequestedAt < now.AddMinutes(-15))));
            if (refunds > 0) logger.LogWarning("{RefundCount} refunds require reconciliation.", refunds);
            nextAlert = now.AddMinutes(5);
        }
    }
}

public sealed class ShippingDispatchWorker(IServiceScopeFactory scopes, TimeProvider time, ILogger<ShippingDispatchWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(30), time);
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                using var scope = scopes.CreateScope();
                await scope.ServiceProvider.GetRequiredService<TransportService>().Tick(stoppingToken);
            }
            catch (Exception error) when (!stoppingToken.IsCancellationRequested)
            {
                logger.LogWarning("Shipping dispatch task failed ({ErrorType}).", error.GetType().Name);
            }
        }
    }
}

public sealed class EmailOutboxWorker(IServiceScopeFactory scopes, TimeProvider time, ILogger<EmailOutboxWorker> logger) : BackgroundService
{
    private DateTimeOffset nextAlert;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(30), time);
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                using var scope = scopes.CreateScope();
                var store = scope.ServiceProvider.GetRequiredService<IShopStore>();
                var smtp = scope.ServiceProvider.GetRequiredService<SmtpSender>();
                await SendMail(store, smtp, stoppingToken);
            }
            catch (Exception error) when (!stoppingToken.IsCancellationRequested)
            {
                logger.LogWarning("Email outbox worker failed ({ErrorType}).", error.GetType().Name);
            }
        }
    }

    private async Task SendMail(IShopStore store, SmtpSender smtp, CancellationToken token)
    {
        var now = time.GetUtcNow();
        var messages = await store.Read(db => db.List(db.Query<OutboxMessage>().Where(m =>
            m.SentAt == null && m.Attempts < 5 && m.NextAttemptAt <= now).OrderBy(m => m.Id).Take(15)));
        foreach (var message in messages)
        {
            var sent = false;
            try
            {
                await smtp.Send(message.Recipient, message.Subject, message.Body, token);
                sent = true;
            }
            catch (Exception error) when (!token.IsCancellationRequested)
            {
                logger.LogWarning("Email outbox message {MessageId} failed ({ErrorType}).", message.Id, error.GetType().Name);
            }
            await store.Write(async db =>
            {
                var row = await db.First(db.Query<OutboxMessage>().Where(m => m.Id == message.Id));
                if (row is null || row.SentAt is not null) return 0;
                row.Attempts++;
                row.NextAttemptAt = now.AddMinutes(Math.Pow(2, row.Attempts));
                if (sent) row.SentAt = time.GetUtcNow();
                return 0;
            });
        }

        if (now >= nextAlert)
        {
            var pending = await store.Read(db => db.Count(db.Query<OutboxMessage>()
                .Where(m => m.SentAt == null && (m.Attempts >= 5 || m.NextAttemptAt < now.AddMinutes(-15)))));
            if (pending > 0) logger.LogWarning("{JobCount} email jobs require attention.", pending);
            nextAlert = now.AddMinutes(5);
        }
    }
}
