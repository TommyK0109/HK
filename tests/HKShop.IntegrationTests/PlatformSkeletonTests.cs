using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using HKShop.Application;
using HKShop.Domain;
using HKShop.Gateway;
using HKShop.ServiceDefaults;
using HKShop.Web;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Xunit;
using Yarp.ReverseProxy.Forwarder;

namespace HKShop.IntegrationTests;

public sealed class PlatformSkeletonTests
{
    private const string SigningKey = "hkshop-phase2-integration-test-signing-key-32b!";

    [Fact]
    public async Task ServiceDefaults_ProvidesHealthChecksProblemDetailsAndJwtActorAuthorization()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Jwt:Issuer"] = "https://hkshop.identity",
            ["Jwt:Audience"] = "hkshop.services",
            ["Jwt:SigningKey"] = SigningKey
        });

        builder.AddHKShopDefaults("HKShop.TestService");
        builder.Services.AddHealthChecks()
            .AddCheck("custom-ready", () => HealthCheckResult.Healthy(), tags: ["ready"]);

        await using var app = builder.Build();
        app.UseExceptionHandler();
        app.UseAuthentication();
        app.UseAuthorization();
        app.MapHKShopHealthChecks();

        app.MapGet("/internal/test/rule-error", () =>
        {
            throw new ShopRuleException("Insufficient offer stock for reservation.");
        });

        app.MapGet("/internal/test/customer", async (IShopRequestContext req) =>
        {
            var actor = await req.Actor();
            actor.RequireCustomer();
            return Results.Ok(new { actor.UserId, actor.Email });
        }).RequireAuthorization(ShopPolicies.CommerceCustomer);

        app.MapGet("/internal/test/admin", async (IShopRequestContext req) =>
        {
            var actor = await req.Actor();
            actor.RequireAdmin();
            return Results.Ok(new { actor.UserId, actor.Admin });
        }).RequireAuthorization(ShopPolicies.EOfficeAdmin);

        await app.StartAsync();
        using var client = app.GetTestClient();

        using var live = await client.GetAsync("/health/live");
        Assert.Equal(HttpStatusCode.OK, live.StatusCode);

        using var ready = await client.GetAsync("/health/ready");
        Assert.Equal(HttpStatusCode.OK, ready.StatusCode);

        using var problemResponse = await client.GetAsync("/internal/test/rule-error");
        Assert.Equal(HttpStatusCode.BadRequest, problemResponse.StatusCode);
        var problem = await problemResponse.Content.ReadFromJsonAsync<ProblemDetails>();
        Assert.NotNull(problem);
        Assert.Equal(400, problem.Status);
        Assert.Equal("Insufficient offer stock for reservation.", problem.Detail);

        using var unauthenticated = await client.GetAsync("/internal/test/customer");
        Assert.Equal(HttpStatusCode.Unauthorized, unauthenticated.StatusCode);

        var jwtSettings = app.Services.GetRequiredService<JwtSettings>();
        var customerToken = ShopActorFactory.CreateJwtToken(
            new ShopActor("cust-42", "cust42@example.test", Verified: true, Staff: false, Admin: false),
            jwtSettings);

        using var customerRequest = new HttpRequestMessage(HttpMethod.Get, "/internal/test/customer");
        customerRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", customerToken);
        using var customerOk = await client.SendAsync(customerRequest);
        Assert.Equal(HttpStatusCode.OK, customerOk.StatusCode);

        using var customerToAdmin = new HttpRequestMessage(HttpMethod.Get, "/internal/test/admin");
        customerToAdmin.Headers.Authorization = new AuthenticationHeaderValue("Bearer", customerToken);
        using var forbidden = await client.SendAsync(customerToAdmin);
        Assert.Equal(HttpStatusCode.Forbidden, forbidden.StatusCode);

        var adminToken = ShopActorFactory.CreateJwtToken(
            new ShopActor("admin-1", "admin@example.test", Verified: true, Staff: true, Admin: true)
            {
                Mfa = true,
                HasEmployeeRole = true
            },
            jwtSettings);
        using var adminRequest = new HttpRequestMessage(HttpMethod.Get, "/internal/test/admin");
        adminRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", adminToken);
        using var adminOk = await client.SendAsync(adminRequest);
        Assert.Equal(HttpStatusCode.OK, adminOk.StatusCode);
    }

    [Fact]
    public async Task Gateway_RoutesToWebPaymentsAndShippingAndEnforcesHeadersAndRateLimits()
    {
        var recorded = new List<(string Authority, string Path, string Method)>();

        using var factory = new WebApplicationFactory<GatewayAssemblyMarker>()
            .WithWebHostBuilder(builder =>
            {
                builder.UseSetting("Destinations:Web", "http://web.internal")
                    .UseSetting("Destinations:Payments", "http://payments.internal")
                    .UseSetting("Destinations:Shipping", "http://shipping.internal")
                    .UseSetting("HttpsRedirection:Enabled", "false")
                    .UseSetting("RateLimiting:CustomerWritesPermitLimit", "2")
                    .ConfigureTestServices(services =>
                    {
                        services.AddSingleton<IForwarderHttpClientFactory>(new RecordingForwarderClientFactory(recorded));
                    });
            });

        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            BaseAddress = new Uri("https://localhost"),
            AllowAutoRedirect = false
        });

        using var live = await client.GetAsync("/health/live");
        Assert.Equal(HttpStatusCode.OK, live.StatusCode);

        using var home = await client.GetAsync("/");
        Assert.Equal(HttpStatusCode.OK, home.StatusCode);
        Assert.Equal("nosniff", home.Headers.GetValues("X-Content-Type-Options").Single());
        Assert.Equal("same-origin", home.Headers.GetValues("Referrer-Policy").Single());
        Assert.Equal(("http://web.internal", "/", "GET"), recorded.Last());

        using var sepayIpn = await client.PostAsJsonAsync("/payments/sepay/ipn", new { });
        Assert.Equal(HttpStatusCode.OK, sepayIpn.StatusCode);
        Assert.Equal("no-store", sepayIpn.Headers.CacheControl?.ToString());
        Assert.Equal(("http://payments.internal", "/payments/sepay/ipn", "POST"), recorded.Last());

        using var sepayReturn = await client.GetAsync($"/payments/sepay/return/{Guid.NewGuid():D}");
        Assert.Equal(HttpStatusCode.OK, sepayReturn.StatusCode);
        Assert.Equal("http://payments.internal", recorded.Last().Authority);

        using var bankWebhook = await client.PostAsJsonAsync("/payments/transport/bank-webhook", new { });
        Assert.Equal(HttpStatusCode.OK, bankWebhook.StatusCode);
        Assert.Equal(("http://shipping.internal", "/payments/transport/bank-webhook", "POST"), recorded.Last());

        using var write1 = await client.PostAsync("/api/commerce/cart", new StringContent(""));
        using var write2 = await client.PostAsync("/api/commerce/cart", new StringContent(""));
        using var write3 = await client.PostAsync("/api/commerce/cart", new StringContent(""));

        Assert.Equal(HttpStatusCode.OK, write1.StatusCode);
        Assert.Equal(HttpStatusCode.OK, write2.StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests, write3.StatusCode);
    }

    private sealed class RecordingForwarderClientFactory(List<(string Authority, string Path, string Method)> calls)
        : IForwarderHttpClientFactory
    {
        public HttpMessageInvoker CreateClient(ForwarderHttpClientContext context) =>
            new(new RecordingHandler(calls), disposeHandler: true);

        private sealed class RecordingHandler(List<(string Authority, string Path, string Method)> calls) : HttpMessageHandler
        {
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                var uri = request.RequestUri!;
                calls.Add((uri.GetLeftPart(UriPartial.Authority), uri.AbsolutePath, request.Method.Method));
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("{\"ok\":true}", System.Text.Encoding.UTF8, "application/json")
                });
            }
        }
    }
}
