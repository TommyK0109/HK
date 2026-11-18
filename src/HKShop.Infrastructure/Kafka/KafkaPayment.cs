using HKShop.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace HKShop.Infrastructure.Kafka;
using Microsoft.AspNetCore;
using Confluent.Kafka;
public class KafkaPaymentSettings
{
    public string BootstrapServers { get; set; } = "";
    public string PaymentTopic { get; set; } = "hkshop.payment.requests.v1";
    public string PaymentGroupId { get; set; } = "hkshop.payment.v1";


}
public sealed class ExecutePaymentPublisher(KafkaPaymentSettings settings, TimeProvider time, IServiceScopeFactory scopes,
    IDbContextFactory<ShopDbContext> factory, ILogger<ExecutePaymentPublisher> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var config = new ProducerConfig
        {
            BootstrapServers = settings.BootstrapServers,
            EnableIdempotence = true,
            Acks = Acks.All,
            MessageTimeoutMs = 5000,
            AllowAutoCreateTopics = false
        };
        var nextAgeCheck = DateTimeOffset.MinValue;
        var partition = new TopicPartition(settings.PaymentTopic, new Partition(0));
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var producer = new ProducerBuilder<Null, string>(config).Build();
                while (!stoppingToken.IsCancellationRequested)
                {
                    using var scope = scopes.CreateScope();
                    var expired = await scope.ServiceProvider.GetRequiredService<PaymentRequests>().Expire(stoppingToken);
                    if (expired > 0) logger.LogInformation("Expired {Count} pending payment requests.", expired);
                    await using var db = await factory.CreateDbContextAsync(stoppingToken);
                    var pending = db.Set<PaymentRequest>().Where(r => r.Status == PaymentRequestStatus.Pending);
                    var now = time.GetUtcNow();
                    if (now >= nextAgeCheck)
                    {
                        nextAgeCheck = now.AddMinutes(1);
                        var oldest = await pending.Select(r => (DateTimeOffset?)r.CreatedAt).MinAsync(stoppingToken);
                        if (oldest is not null && now - oldest.Value >= TimeSpan.FromSeconds(30))
                            logger.LogWarning("Oldest pending payment request is {AgeSeconds:F0} seconds old.",
                                (now - oldest.Value).TotalSeconds);
                    }
                    var ids = await pending.Where(r => r.PublishedAt == null && r.ExpiresAt > now)
                        .OrderBy(r => r.CreatedAt).ThenBy(r => r.Id).Select(r => r.Id).Take(50)
                        .ToListAsync(stoppingToken);
                    foreach (var id in ids)
                    {
