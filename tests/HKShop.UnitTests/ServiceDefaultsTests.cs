using System.Diagnostics;
using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Security.Claims;
using Confluent.Kafka;
using HKShop.Application;
using HKShop.Domain;
using HKShop.ServiceDefaults;
using Microsoft.AspNetCore.Http;
using Xunit;

namespace HKShop.UnitTests;

public sealed class ServiceDefaultsTests
{
    [Fact]
    public void ShopActorFactory_ReconstructsActorFromJwtClaimsAndEnforcesRoleRules()
    {
        var jwtSettings = new JwtSettings
        {
            Issuer = "https://hkshop.identity.test",
            Audience = "hkshop.services.test",
            SigningKey = "0123456789abcdef0123456789abcdef0123456789abcdef"
        };

        var customer = new ShopActor("cust-1", "buyer@example.test", Verified: true, Staff: false, Admin: false);
        var customerToken = ShopActorFactory.CreateJwtToken(customer, jwtSettings);
        var customerPrincipal = ReadJwtPrincipal(customerToken);
        var resolvedCustomer = ShopActorFactory.FromClaims(customerPrincipal);

        Assert.Equal("cust-1", resolvedCustomer.UserId);
        Assert.Equal("buyer@example.test", resolvedCustomer.Email);
        Assert.True(resolvedCustomer.Verified);
        Assert.False(resolvedCustomer.IsEmployee);
        resolvedCustomer.RequireCustomer();
        Assert.Throws<ShopRuleException>(resolvedCustomer.RequireStaff);

        var staff = new ShopActor("staff-1", "staff@example.test", Verified: true, Staff: true, Admin: false)
        {
            Mfa = true,
            HasEmployeeRole = true
        };
        var staffToken = ShopActorFactory.CreateJwtToken(staff, jwtSettings);
        var resolvedStaff = ShopActorFactory.FromClaims(ReadJwtPrincipal(staffToken));

        Assert.True(resolvedStaff.Staff);
        Assert.True(resolvedStaff.Mfa);
        Assert.True(resolvedStaff.IsEmployee);
        resolvedStaff.RequireStaff();
        Assert.Throws<ShopRuleException>(resolvedStaff.RequireCustomer);
        Assert.Throws<ShopRuleException>(resolvedStaff.RequireAdmin);

        var staffWithoutMfa = new ClaimsPrincipal(new ClaimsIdentity(
        [
            new Claim("sub", "staff-no-mfa"),
            new Claim("email", "staff2@example.test"),
            new Claim("email_verified", "true"),
            new Claim("role", "StoreStaff")
        ], "Bearer"));
        var resolvedNoMfa = ShopActorFactory.FromClaims(staffWithoutMfa);
        Assert.False(resolvedNoMfa.Staff);
        Assert.True(resolvedNoMfa.HasEmployeeRole);
        Assert.True(resolvedNoMfa.IsEmployee);
        Assert.Throws<ShopRuleException>(resolvedNoMfa.RequireStaff);
        Assert.Throws<ShopRuleException>(resolvedNoMfa.RequireCustomer);
    }

    [Fact]
    public void KafkaTelemetry_PropagatesW3CTraceContextAndBaggageThroughHeaders()
    {
        HKShopTelemetry.ConfigureW3CPropagation();

        using var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == HKShopTelemetry.ActivitySourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded
        };
        ActivitySource.AddActivityListener(listener);

        var headers = new Headers();
        ActivityTraceId parentTraceId;
        ActivitySpanId producerSpanId;

        using (var httpActivity = HKShopTelemetry.ActivitySource.StartActivity("POST /checkout", ActivityKind.Server))
        {
            Assert.NotNull(httpActivity);
            httpActivity.SetBaggage("hkshop.order.id", "ord-123");
            parentTraceId = httpActivity.TraceId;

            using var producerActivity = KafkaTelemetry.StartProducerActivity(
                "OrderConfirmationRelay.Publish", headers, "hkshop.ordering.events.v1", "ord-123");
            Assert.NotNull(producerActivity);
            Assert.Equal(parentTraceId, producerActivity.TraceId);
            producerSpanId = producerActivity.SpanId;
        }

        using var consumerActivity = KafkaTelemetry.StartConsumerActivity(
            headers, "ConfirmationConsumer.Process", "hkshop.ordering.events.v1", "ord-123");

        Assert.NotNull(consumerActivity);
        Assert.Equal(parentTraceId, consumerActivity.TraceId);
        Assert.Equal(producerSpanId, consumerActivity.ParentSpanId);
        Assert.Equal("ord-123", consumerActivity.GetBaggageItem("hkshop.order.id"));
    }

    [Fact]
    public async Task OutgoingHttpHandler_AttachesIdempotencyKeyAndForwardsBearerToken()
    {
        var httpContext = new DefaultHttpContext { TraceIdentifier = "trace-xyz" };
        httpContext.Request.Headers.Authorization = "Bearer test-jwt-token";
        var accessor = new HttpContextAccessor { HttpContext = httpContext };
        var capture = new CaptureHandler();
        using var handler = new HKShopOutgoingHttpHandler(accessor, new PassThroughBffAccessTokenStore())
        {
            InnerHandler = capture
        };
        using var client = new HttpClient(handler) { BaseAddress = new Uri("https://service.internal") };

        using var getResponse = await client.GetAsync("/internal/catalog/quote");
        Assert.False(capture.LastRequest!.Headers.Contains(HKShopOutgoingHttpHandler.IdempotencyKeyHeader));
        Assert.Equal("Bearer test-jwt-token", capture.LastRequest.Headers.Authorization?.ToString());
        Assert.Equal("trace-xyz", capture.LastRequest.Headers.GetValues(HKShopOutgoingHttpHandler.CorrelationIdHeader).Single());

        using var postResponse = await client.PostAsync("/internal/catalog/reserve", new StringContent("{}"));
        Assert.True(capture.LastRequest.Headers.TryGetValues(HKShopOutgoingHttpHandler.IdempotencyKeyHeader, out var keys));
        Assert.True(Guid.TryParse(keys.Single(), out _));
    }

    private static ClaimsPrincipal ReadJwtPrincipal(string token)
    {
        var jwt = new JwtSecurityTokenHandler().ReadJwtToken(token);
        return new ClaimsPrincipal(new ClaimsIdentity(jwt.Claims, "Bearer", "sub", "role"));
    }

    private sealed class CaptureHandler : HttpMessageHandler
    {
        public HttpRequestMessage? LastRequest { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastRequest = request;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
        }
    }
}
