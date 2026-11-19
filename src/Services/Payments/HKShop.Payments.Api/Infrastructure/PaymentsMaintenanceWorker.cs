using HKShop.Payments.Api.Application;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace HKShop.Payments.Api.Infrastructure;

public sealed class PaymentsMaintenanceWorker(
    IServiceScopeFactory scopes,
    TimeProvider time,
    ILogger<PaymentsMaintenanceWorker> logger) : BackgroundService
{
    protected override Task ExecuteAsync(CancellationToken stoppingToken) => Task.WhenAll(
        LoopAsync("expiry", (p, ct) => p.ExpireDuePaymentsAsync(ct), stoppingToken),
        LoopAsync("reconciliation", ReconcilePaymentsAsync, stoppingToken),
        LoopAsync("refunds", ReconcileRefundsAsync, stoppingToken),
        LoopAsync("settlement", (p, ct) => p.ReleaseDueSettlementsAsync(ct), stoppingToken),
        LoopAsync("payouts", ReconcilePayoutsAsync, stoppingToken));

    private async Task LoopAsync(string name, Func<PaymentsProcessor, CancellationToken, Task> work, CancellationToken token)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(30), time);
        while (await timer.WaitForNextTickAsync(token))
        {
            try
            {
                using var scope = scopes.CreateScope();
                var processor = scope.ServiceProvider.GetRequiredService<PaymentsProcessor>();
                await work(processor, token);
            }
            catch (Exception error) when (!token.IsCancellationRequested)
            {
                logger.LogWarning("Payments background task {JobName} failed ({ErrorType}).", name, error.GetType().Name);
            }
        }
    }

    private async Task ReconcilePaymentsAsync(PaymentsProcessor processor, CancellationToken token)
    {
        if (!processor.OnlineAvailable) return;
        foreach (var job in await processor.DueQueriesAsync(token))
        {
            try { await processor.ReconcilePaymentAsync(job.OrderId, job.Number, token); }
            catch (Exception error) when (!token.IsCancellationRequested)
            {
                logger.LogWarning("SePay reconciliation failed for {OrderId} ({ErrorType}).", job.OrderId, error.GetType().Name);
            }
        }
    }

    private async Task ReconcileRefundsAsync(PaymentsProcessor processor, CancellationToken token)
    {
        foreach (var id in await processor.DueRefundsAsync(token))
        {
            try { await processor.ReconcileRefundAsync(id, token); }
            catch (Exception error) when (!token.IsCancellationRequested)
            {
                logger.LogWarning("Refund reconciliation failed for {RefundId} ({ErrorType}).", id, error.GetType().Name);
            }
        }
    }

    private async Task ReconcilePayoutsAsync(PaymentsProcessor processor, CancellationToken token)
    {
        foreach (var id in await processor.DuePayoutsAsync(token))
        {
            try { await processor.ReconcilePayoutAsync(id, token); }
            catch (Exception error) when (!token.IsCancellationRequested)
            {
                logger.LogWarning("Payout reconciliation failed for {PayoutId} ({ErrorType}).", id, error.GetType().Name);
            }
        }
    }
}
