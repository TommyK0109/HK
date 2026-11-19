using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace HKShop.Infrastructure.Kafka;

public static class KafkaCheckoutRegistration
{
    public static IServiceCollection AddKafkaCheckout(this IServiceCollection services, IConfiguration configuration)
    {
        var settings = configuration.GetSection("Kafka").Get<KafkaCheckoutSettings>() ?? new();
        if (string.IsNullOrWhiteSpace(settings.BootstrapServers) || string.IsNullOrWhiteSpace(settings.CheckoutTopic) ||
            string.IsNullOrWhiteSpace(settings.CheckoutGroupId))
            throw new InvalidOperationException("Configure Kafka:BootstrapServers, Kafka:CheckoutTopic and Kafka:CheckoutGroupId.");

        services.AddSingleton(settings);
        services.AddScoped<CheckoutRequests>();
        services.AddHostedService<CheckoutPublisher>();
        services.AddHostedService<CheckoutConsumer>();
        services.AddHostedService<OrderConfirmationRelay>();
        return services;
    }
}
