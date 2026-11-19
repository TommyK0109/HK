using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace HKShop.Messaging;

public static class ServiceMessagingExtensions
{
    public static IServiceCollection AddHKShopMessaging<TDbContext, THandler>(
        this IServiceCollection services,
        IConfiguration configuration,
        string producerTopic,
        string consumerName,
        string consumerGroupId,
        params string[] subscribedTopics)
        where TDbContext : DbContext
        where THandler : class, IIntegrationEventHandler<TDbContext>
    {
        var brokers = configuration["Kafka:BootstrapServers"] ?? "";
        services.AddScoped<IIntegrationEventHandler<TDbContext>, THandler>();
        services.AddSingleton(new OutboxRelayOptions(brokers, producerTopic, consumerName));
        services.AddSingleton(new InboxConsumerOptions(brokers, consumerGroupId, consumerName, subscribedTopics));
        services.AddHostedService<OutboxRelayWorker<TDbContext>>();
        services.AddHostedService<InboxConsumerWorker<TDbContext>>();
        return services;
    }
}
