using HKShop.Gateway;
using HKShop.ServiceDefaults;
using Yarp.ReverseProxy.Configuration;

var builder = WebApplication.CreateBuilder(args);
builder.AddHKShopDefaults("HKShop.Gateway");
builder.Services.AddReverseProxy()
    .LoadFromMemory(GatewayConfig.BuildRoutes(), GatewayConfig.BuildClusters(builder.Configuration));

var app = builder.Build();
app.UseForwardedHeaders();
app.UseExceptionHandler();
if (!app.Environment.IsDevelopment()) app.UseHsts();
if (app.Configuration.GetValue("HttpsRedirection:Enabled", true))
    app.UseHttpsRedirection();
app.UseHKShopSecurityHeaders();
app.UseHKShopRequestLogging();
app.UseAuthentication();
app.UseAuthorization();
app.UseRateLimiter();
app.MapHKShopHealthChecks();
app.MapReverseProxy();
app.Run();

namespace HKShop.Gateway
{
    public sealed class GatewayAssemblyMarker;

    public static class GatewayConfig
    {
        public const string WebClusterId = "web";
        public const string CatalogClusterId = "catalog";
        public const string PaymentsClusterId = "payments";
        public const string ShippingClusterId = "shipping";

        public static IReadOnlyList<RouteConfig> BuildRoutes() =>
        [
            new RouteConfig
            {
                RouteId = "catalog-media",
                ClusterId = CatalogClusterId,
                Order = 1,
                Match = new RouteMatch { Path = "/media/{**catch-all}" }
            },
            new RouteConfig
            {
                RouteId = "payments-sepay",
                ClusterId = PaymentsClusterId,
                Order = 1,
                Match = new RouteMatch { Path = "/payments/sepay/{**catch-all}" }
            },
            new RouteConfig
            {
                RouteId = "shipping-bank-webhook",
                ClusterId = ShippingClusterId,
                Order = 1,
                Match = new RouteMatch { Path = "/payments/transport/bank-webhook" }
            },
            new RouteConfig
            {
                RouteId = "customer-writes",
                ClusterId = WebClusterId,
                Order = 2,
                RateLimiterPolicy = ServiceDefaultsExtensions.CustomerWritesPolicy,
                Match = new RouteMatch { Path = "/api/{**catch-all}", Methods = ["POST"] }
            },
            new RouteConfig
            {
                RouteId = "web",
                ClusterId = WebClusterId,
                Order = 3,
                Match = new RouteMatch { Path = "{**catch-all}" }
            }
        ];

        public static IReadOnlyList<ClusterConfig> BuildClusters(IConfiguration configuration)
        {
            var webAddress = ResolveAddress(configuration, WebClusterId, "Destinations:Web", "http://localhost:8080");
            var catalogAddress = ResolveAddress(configuration, CatalogClusterId, "Destinations:Catalog", webAddress);
            var paymentsAddress = ResolveAddress(configuration, PaymentsClusterId, "Destinations:Payments", webAddress);
            var shippingAddress = ResolveAddress(configuration, ShippingClusterId, "Destinations:Shipping", webAddress);

            return
            [
                CreateCluster(WebClusterId, webAddress),
                CreateCluster(CatalogClusterId, catalogAddress),
                CreateCluster(PaymentsClusterId, paymentsAddress),
                CreateCluster(ShippingClusterId, shippingAddress)
            ];
        }

        private static string ResolveAddress(IConfiguration configuration, string clusterId, string key, string fallback) =>
            configuration[$"ReverseProxy:Clusters:{clusterId}:Destinations:default:Address"]
            ?? configuration[key]
            ?? fallback;

        private static ClusterConfig CreateCluster(string clusterId, string address) =>
            new()
            {
                ClusterId = clusterId,
                Destinations = new Dictionary<string, DestinationConfig>(StringComparer.OrdinalIgnoreCase)
                {
                    ["default"] = new() { Address = address }
                }
            };
    }
}
