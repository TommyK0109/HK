using Confluent.Kafka;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging;

namespace HKShop.ServiceDefaults;

public sealed class DbContextReadinessCheck<TDbContext>(IDbContextFactory<TDbContext> factory, ILogger<DbContextReadinessCheck<TDbContext>> log)
    : IHealthCheck where TDbContext : DbContext
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        try
        {
            await using var db = await factory.CreateDbContextAsync(cancellationToken);
            if (await db.Database.CanConnectAsync(cancellationToken) &&
                !(await db.Database.GetPendingMigrationsAsync(cancellationToken)).Any())
                return HealthCheckResult.Healthy();
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            log.LogWarning(ex, "Database readiness check failed for {DbContext}.", typeof(TDbContext).Name);
            return HealthCheckResult.Unhealthy("Database readiness check failed.", ex);
        }

        log.LogWarning("SQL Server or the required {DbContext} schema is unavailable.", typeof(TDbContext).Name);
        return HealthCheckResult.Unhealthy();
    }
}

public sealed class KafkaReadinessCheck(IConfiguration configuration, ILogger<KafkaReadinessCheck> log) : IHealthCheck
{
    public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        var brokers = configuration["Kafka:BootstrapServers"];
        if (string.IsNullOrWhiteSpace(brokers))
            return Task.FromResult(HealthCheckResult.Healthy("Kafka bootstrap servers not configured."));

        try
        {
            using var admin = new AdminClientBuilder(new AdminClientConfig
            {
                BootstrapServers = brokers,
                SocketTimeoutMs = 2000
            }).Build();
            var metadata = admin.GetMetadata(TimeSpan.FromSeconds(2));
            if (metadata.Brokers.Count > 0)
                return Task.FromResult(HealthCheckResult.Healthy());
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            log.LogWarning("Kafka readiness check failed ({ErrorType}).", ex.GetType().Name);
            return Task.FromResult(HealthCheckResult.Unhealthy("Kafka broker is unreachable.", ex));
        }

        return Task.FromResult(HealthCheckResult.Unhealthy("No Kafka brokers reported metadata."));
    }
}

public static class HKShopHealthCheckExtensions
{
    public static IHealthChecksBuilder AddHKShopDbReadiness<TDbContext>(this IHealthChecksBuilder builder, string name = "sql")
        where TDbContext : DbContext =>
        builder.AddCheck<DbContextReadinessCheck<TDbContext>>(name, tags: ["ready"]);

    public static IHealthChecksBuilder AddHKShopKafkaReadiness(this IHealthChecksBuilder builder, string name = "kafka") =>
        builder.AddCheck<KafkaReadinessCheck>(name, tags: ["ready"]);
}
