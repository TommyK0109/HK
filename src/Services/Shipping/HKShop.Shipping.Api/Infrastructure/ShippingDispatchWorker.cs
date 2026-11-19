using HKShop.Shipping.Api.Application;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace HKShop.Shipping.Api.Infrastructure;

public sealed class ShippingDispatchWorker(
    IServiceScopeFactory scopes,
    TimeProvider time,
    ILogger<ShippingDispatchWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(30), time);
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                using var scope = scopes.CreateScope();
                await scope.ServiceProvider.GetRequiredService<ShippingOrderLifecycle>().TickAsync(stoppingToken);
            }
            catch (Exception error) when (!stoppingToken.IsCancellationRequested)
            {
                logger.LogWarning("Shipping dispatch task failed ({ErrorType}).", error.GetType().Name);
            }
        }
    }
}
