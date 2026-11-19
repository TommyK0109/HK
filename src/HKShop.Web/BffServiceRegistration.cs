using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text.Json;
using HKShop.Application;
using HKShop.Application.Accounts;
using HKShop.Application.Checkout;
using HKShop.Application.Orders;
using HKShop.Application.Payments;
using HKShop.Application.Reporting;
using HKShop.Application.Transportation;
using HKShop.Contracts.Clients;
using HKShop.Contracts.Ports;
using HKShop.Domain;
using HKShop.Infrastructure.Identity;
using HKShop.Infrastructure.Kafka;
using HKShop.ServiceDefaults;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace HKShop.Web;

public sealed class BffSessionTokenStore(
    IHttpClientFactory clients,
    JwtSettings jwtSettings,
    IConfiguration configuration,
    TimeProvider time,
    ILogger<BffSessionTokenStore> logger) : IBffAccessTokenStore
{
    public async ValueTask<string?> GetAccessTokenAsync(HttpContext context, CancellationToken cancellationToken = default)
    {
        if (context.Request.Headers.Authorization.ToString() is { Length: > 7 } auth &&
            auth.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
            return auth["Bearer ".Length..].Trim();

        var authResult = context.Features.Get<IAuthenticateResultFeature>()?.AuthenticateResult;
        if (authResult?.Properties is { } props &&
            props.GetTokenValue("access_token") is { Length: > 0 } accessToken)
        {
            var expiresText = props.GetTokenValue("expires_at");
            var refreshToken = props.GetTokenValue("refresh_token");
            if (!string.IsNullOrWhiteSpace(refreshToken) &&
                DateTimeOffset.TryParse(expiresText, out var expiresAt) &&
                expiresAt <= time.GetUtcNow().AddSeconds(60) &&
                !string.IsNullOrWhiteSpace(configuration["Services:Identity:BaseUrl"]))
            {
                try
                {
                    using var http = clients.CreateClient("IdentityTokenRefresh");
                    var refreshed = await new IdentityServiceClient(http).RefreshTokenAsync(refreshToken, cancellationToken);
                    if (!string.IsNullOrWhiteSpace(refreshed.AccessToken))
                    {
                        props.UpdateTokenValue("access_token", refreshed.AccessToken);
                        if (!string.IsNullOrWhiteSpace(refreshed.RefreshToken))
                            props.UpdateTokenValue("refresh_token", refreshed.RefreshToken);
                        if (refreshed.ExpiresAt is { } nextExpiry)
                            props.UpdateTokenValue("expires_at", nextExpiry.ToString("O"));
                        if (!context.Response.HasStarted && authResult.Ticket is { } ticket)
                            await context.SignInAsync(ticket.AuthenticationScheme, ticket.Principal, props);
                        return refreshed.AccessToken;
                    }
                }
                catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
                {
                    logger.LogWarning("BFF token refresh failed ({ErrorType}).", ex.GetType().Name);
                }
            }
            return accessToken;
        }

        // Hybrid strangler transition: mint a short-lived actor JWT for authenticated cookie sessions.
        if (context.User.Identity?.IsAuthenticated == true &&
            !string.IsNullOrWhiteSpace(jwtSettings.SigningKey) &&
            jwtSettings.SigningKey.Length >= 32)
        {
            var resolver = context.RequestServices.GetService<IShopUserResolver>();
            var actor = resolver is not null
                ? await resolver.LoadActorAsync(context.User)
                : ShopActorFactory.FromClaims(context.User);
            if (!string.IsNullOrEmpty(actor.UserId))
                return ShopActorFactory.CreateJwtToken(actor, jwtSettings, time.GetUtcNow(), TimeSpan.FromMinutes(2));
        }

        return null;
    }
}

public sealed class HttpCheckoutRequestDispatcher(OrderingServiceClient ordering) : ICheckoutRequestDispatcher
{
    public async Task<CheckoutResult> Submit(ShopActor actor, CheckoutQuote quote)
    {
        CheckoutService.ValidateSubmission(actor, quote);
        var placed = await ordering.PlaceOrderAsync(quote);
        return new CheckoutResult(quote.SubmissionKey, CheckoutRequestStatus.Placed, placed.OrderId, null);
    }

    public async Task<CheckoutResult?> Get(ShopActor actor, Guid id)
    {
        if (!actor.Verified || string.IsNullOrEmpty(actor.UserId)) return null;
        var status = await ordering.GetOrderStatusAsync(id);
        return status is null ? null : new CheckoutResult(id, CheckoutRequestStatus.Placed, status.OrderId, null);
    }
}

internal sealed class BffJwtCookieSession(
    IHttpContextAccessor accessor,
    IdentityServiceClient identity,
    IDataProtectionProvider protection)
{
    private const string MfaCookieName = "hkshop.bff.mfa";
    private readonly IDataProtector protector = protection.CreateProtector("HKShop.BffMfaChallenge.v1");

    public async Task<string> SignInTokensAsync(TokenPairResponse tokens)
    {
        var http = accessor.HttpContext ?? throw new InvalidOperationException("No active HTTP context.");
        var jwt = new JwtSecurityTokenHandler().ReadJwtToken(tokens.AccessToken!);
        var claimsIdentity = new ClaimsIdentity(jwt.Claims, IdentityConstants.ApplicationScheme, "sub", "role");
        var principal = new ClaimsPrincipal(claimsIdentity);
        var props = new AuthenticationProperties();
        var store = new List<AuthenticationToken> { new() { Name = "access_token", Value = tokens.AccessToken! } };
        if (!string.IsNullOrWhiteSpace(tokens.RefreshToken))
            store.Add(new() { Name = "refresh_token", Value = tokens.RefreshToken });
        if (tokens.ExpiresAt is { } expiresAt)
            store.Add(new() { Name = "expires_at", Value = expiresAt.ToString("O") });
        props.StoreTokens(store);
        await http.SignInAsync(IdentityConstants.ApplicationScheme, principal, props);
        return jwt.Claims.FirstOrDefault(c => c.Type == "sub")?.Value ?? "";
    }

    public void StorePendingMfa(string email, string password, string platform)
    {
        var http = accessor.HttpContext ?? throw new InvalidOperationException("No active HTTP context.");
        var payload = protector.Protect(JsonSerializer.Serialize(new PendingMfa(email, password, platform)));
        http.Response.Cookies.Append(MfaCookieName, payload, new CookieOptions
        {
            HttpOnly = true,
            Secure = http.Request.IsHttps,
            SameSite = SameSiteMode.Lax,
            MaxAge = TimeSpan.FromMinutes(5)
        });
    }

    public PendingMfa? ReadPendingMfa()
    {
        var http = accessor.HttpContext;
        if (http is null || !http.Request.Cookies.TryGetValue(MfaCookieName, out var raw)) return null;
        try { return JsonSerializer.Deserialize<PendingMfa>(protector.Unprotect(raw)); }
        catch { return null; }
    }

    public void ClearPendingMfa() => accessor.HttpContext?.Response.Cookies.Delete(MfaCookieName);

    public async Task SignOutAndRevokeAsync()
    {
        var http = accessor.HttpContext;
        if (http is null) return;
        var refresh = await http.GetTokenAsync(IdentityConstants.ApplicationScheme, "refresh_token");
        if (!string.IsNullOrWhiteSpace(refresh))
        {
            try { await identity.RevokeTokenAsync(refresh, http.RequestAborted); }
            catch { }
        }
        ClearPendingMfa();
        await http.SignOutAsync(IdentityConstants.ApplicationScheme);
    }

    internal sealed record PendingMfa(string Email, string Password, string Platform);
}

internal sealed class BffCommerceAuthService(
    IdentityServiceClient identity,
    BffJwtCookieSession session,
    CommerceAuthService fallback) : ICommerceAuthService
{
    public async Task<bool> RegisterAsync(RegisterInput input, string baseOrigin, CancellationToken cancellationToken)
    {
        await identity.RegisterAsync(new(input.Email.Trim(), input.DisplayName.Trim(), input.Password), cancellationToken);
        return true;
    }

    public Task<bool> ConfirmEmailAsync(string userId, string token) => fallback.ConfirmEmailAsync(userId, token);
    public Task<bool> ResendConfirmationAsync(string? email, string baseOrigin, CancellationToken cancellationToken) => fallback.ResendConfirmationAsync(email, baseOrigin, cancellationToken);

    public async Task<CommerceLoginOutcome> LoginAsync(string? email, string? password)
    {
        var result = await identity.IssueTokenAsync(new(email ?? "", password ?? "", "commerce"));
        if (result.RequiresTwoFactor)
        {
            session.StorePendingMfa(email ?? "", password ?? "", "commerce");
            return new CommerceLoginOutcome(null, true);
        }
        var userId = await session.SignInTokensAsync(result);
        return new CommerceLoginOutcome(userId, false);
    }

    public async Task<string?> CompleteMfaAsync(string? code)
    {
        var pending = session.ReadPendingMfa();
        if (pending is null || !string.Equals(pending.Platform, "commerce", StringComparison.OrdinalIgnoreCase))
            return null;
        var result = await identity.IssueTokenAsync(new(pending.Email, pending.Password, "commerce", code));
        session.ClearPendingMfa();
        return await session.SignInTokensAsync(result);
    }

    public Task PrepareMfaAsync(ClaimsPrincipal principal, string? password) => fallback.PrepareMfaAsync(principal, password);
    public Task EnableMfaAsync(ClaimsPrincipal principal, string? code) => fallback.EnableMfaAsync(principal, code);
    public Task LogoutAsync() => session.SignOutAndRevokeAsync();
    public Task<bool> ForgotPasswordAsync(string? email, string baseOrigin, CancellationToken cancellationToken) => fallback.ForgotPasswordAsync(email, baseOrigin, cancellationToken);
    public Task ResetPasswordAsync(string? userId, string? token, string? password, string? confirmation) => fallback.ResetPasswordAsync(userId, token, password, confirmation);

    public Task<ShopUserIdentity> RequireCurrentUserAsync(ClaimsPrincipal principal)
    {
        var actor = ShopActorFactory.FromClaims(principal);
        if (string.IsNullOrEmpty(actor.UserId)) throw new ShopRuleException("Please sign in again.");
        return Task.FromResult(new ShopUserIdentity(actor.UserId, actor.Email, actor.Email, null, actor.Verified, actor.Mfa, false));
    }

    public Task<string?> GetAuthenticatorKeyAsync(ClaimsPrincipal principal) => fallback.GetAuthenticatorKeyAsync(principal);
}

internal sealed class BffEOfficeAuthService(
    IdentityServiceClient identity,
    BffJwtCookieSession session,
    EOfficeAuthService fallback) : IEOfficeAuthService
{
    public async Task<EOfficeLoginOutcome> LoginAsync(string? email, string? password)
    {
        var result = await identity.IssueTokenAsync(new(email ?? "", password ?? "", "eoffice"));
        if (result.RequiresTwoFactor)
        {
            session.StorePendingMfa(email ?? "", password ?? "", "eoffice");
            return new EOfficeLoginOutcome(result.DefaultPath ?? "/eoffice", true, false);
        }
        if (result.RequiresMfaEnrollment)
            return new EOfficeLoginOutcome(result.DefaultPath ?? "/eoffice", false, true);
        await session.SignInTokensAsync(result);
        return new EOfficeLoginOutcome(result.DefaultPath ?? "/eoffice", false, false);
    }

    public async Task<string?> CompleteMfaAsync(string? code)
    {
        var pending = session.ReadPendingMfa();
        if (pending is null || !string.Equals(pending.Platform, "eoffice", StringComparison.OrdinalIgnoreCase))
            return null;
        var result = await identity.IssueTokenAsync(new(pending.Email, pending.Password, "eoffice", code));
        session.ClearPendingMfa();
        await session.SignInTokensAsync(result);
        return result.DefaultPath ?? "/eoffice";
    }

    public Task PrepareMfaAsync(ClaimsPrincipal principal, string? password) => fallback.PrepareMfaAsync(principal, password);
    public Task EnableMfaAsync(ClaimsPrincipal principal, string? code) => fallback.EnableMfaAsync(principal, code);
    public Task LogoutAsync() => session.SignOutAndRevokeAsync();

    public Task<ShopUserIdentity> RequireCurrentUserAsync(ClaimsPrincipal principal)
    {
        var actor = ShopActorFactory.FromClaims(principal);
        if (string.IsNullOrEmpty(actor.UserId)) throw new ShopRuleException("Please sign in again.");
        return Task.FromResult(new ShopUserIdentity(actor.UserId, actor.Email, actor.Email, null, actor.Verified, actor.Mfa, false));
    }

    public Task<string?> GetAuthenticatorKeyAsync(ClaimsPrincipal principal) => fallback.GetAuthenticatorKeyAsync(principal);
    public Task<bool> SendStaffInvitationEmailAsync(StaffInvitation invitation, string baseOrigin, CancellationToken cancellationToken) => fallback.SendStaffInvitationEmailAsync(invitation, baseOrigin, cancellationToken);
    public Task SetShipperAccessAsync(ShopActor actor, string email, bool active) => identity.SetShipperRoleAsync(email, active);
}

public sealed record BffComposedAuditReport(
    IReadOnlyList<AuditEntryDto> Entries,
    SalesSummary? SalesSummary);

public static class BffServiceRegistration
{
    public static IServiceCollection AddHKShopBffClients(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddSingleton(new ShippingClientOptions
        {
            Enabled = configuration.GetValue("Transport:Enabled", false)
        });

        var hasIdentity = !string.IsNullOrWhiteSpace(configuration["Services:Identity:BaseUrl"]);
        var hasCatalog = !string.IsNullOrWhiteSpace(configuration["Services:Catalog:BaseUrl"]);
        var hasOrdering = !string.IsNullOrWhiteSpace(configuration["Services:Ordering:BaseUrl"]);
        var hasPayments = !string.IsNullOrWhiteSpace(configuration["Services:Payments:BaseUrl"]);
        var hasShipping = !string.IsNullOrWhiteSpace(configuration["Services:Shipping:BaseUrl"]);

        RegisterClient<IdentityServiceClient>(services, configuration["Services:Identity:BaseUrl"], "http://localhost:5101");
        services.AddHttpClient("IdentityTokenRefresh", client =>
            client.BaseAddress = new Uri(configuration["Services:Identity:BaseUrl"] ?? "http://localhost:5101"));
        RegisterClient<CatalogServiceClient>(services, configuration["Services:Catalog:BaseUrl"], "http://localhost:5102");
        RegisterClient<OrderingServiceClient>(services, configuration["Services:Ordering:BaseUrl"], "http://localhost:5103");
        RegisterClient<PaymentsServiceClient>(services, configuration["Services:Payments:BaseUrl"], "http://localhost:5104");
        RegisterClient<ShippingServiceClient>(services, configuration["Services:Shipping:BaseUrl"], "http://localhost:5105");

        services.Replace(ServiceDescriptor.Singleton<IBffAccessTokenStore, BffSessionTokenStore>());

        if (hasShipping)
        {
            services.Replace(ServiceDescriptor.Scoped<IShippingGateway, HttpShippingGateway>());
            services.Replace(ServiceDescriptor.Scoped<IShippingAdminPort, HttpShippingGateway>());
            services.Replace(ServiceDescriptor.Scoped<IShippingShipperPort, HttpShippingGateway>());
        }

        if (hasCatalog)
        {
            services.Replace(ServiceDescriptor.Scoped<ICatalogOperationsPort, HttpCatalogOperationsClient>());
        }

        if (hasIdentity)
        {
            services.Replace(ServiceDescriptor.Scoped<AddressService>(sp =>
                new AddressService(sp.GetRequiredService<IShopStore>(), sp.GetRequiredService<IdentityServiceClient>())));

            if (configuration.GetValue("Identity:UseJwtAuth", false))
            {
                services.AddScoped<CommerceAuthService>();
                services.AddScoped<EOfficeAuthService>();
                services.AddScoped<BffJwtCookieSession>();
                services.Replace(ServiceDescriptor.Scoped<IShopUserResolver, ClaimsShopUserResolver>());
                services.Replace(ServiceDescriptor.Scoped<ICommerceAuthService, BffCommerceAuthService>());
                services.Replace(ServiceDescriptor.Scoped<IEOfficeAuthService, BffEOfficeAuthService>());
            }
        }

        if (hasOrdering)
        {
            services.Replace(ServiceDescriptor.Scoped<CartService>(sp =>
                new CartService(sp.GetRequiredService<IShopStore>(), sp.GetRequiredService<TimeProvider>(), sp.GetRequiredService<OrderingServiceClient>())));
            services.Replace(ServiceDescriptor.Scoped<CheckoutService>(sp =>
                new CheckoutService(
                    sp.GetRequiredService<IShopStore>(),
                    sp.GetRequiredService<ShopSettings>(),
                    sp.GetRequiredService<TimeProvider>(),
                    sp.GetService<TransportSettings>(),
                    sp.GetService<ITransportRouting>(),
                    sp.GetService<IShippingGateway>(),
                    sp.GetRequiredService<OrderingServiceClient>())));
            services.Replace(ServiceDescriptor.Scoped<OrderService>(sp =>
                new OrderService(
                    sp.GetRequiredService<IShopStore>(),
                    sp.GetRequiredService<TimeProvider>(),
                    sp.GetService<IShippingGateway>(),
                    sp.GetRequiredService<OrderingServiceClient>(),
                    hasPayments ? sp.GetRequiredService<PaymentsServiceClient>() : null)));
            services.Replace(ServiceDescriptor.Scoped<ICheckoutRequestDispatcher, HttpCheckoutRequestDispatcher>());
        }

        if (hasPayments)
        {
            services.Replace(ServiceDescriptor.Scoped<PaymentService>(sp =>
                new PaymentService(
                    sp.GetRequiredService<IShopStore>(),
                    sp.GetRequiredService<IPaymentGateway>(),
                    sp.GetRequiredService<TimeProvider>(),
                    sp.GetService<IShippingGateway>(),
                    sp.GetRequiredService<PaymentsServiceClient>())));
        }

        return services;
    }

    public static IEndpointRouteBuilder MapHKShopBffCompositionEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/internal/reports/compose", async (
            DateOnly start,
            DateOnly end,
            IConfiguration configuration,
            IdentityServiceClient identity,
            CatalogServiceClient catalog,
            OrderingServiceClient ordering,
            PaymentsServiceClient payments,
            ShippingServiceClient shipping,
            CancellationToken ct) =>
        {
            var zone = TimeSpan.FromHours(7);
            var from = new DateTimeOffset(start.ToDateTime(TimeOnly.MinValue), zone);
            var to = new DateTimeOffset(end.AddDays(1).ToDateTime(TimeOnly.MinValue), zone);

            var auditTasks = new List<Task<IReadOnlyList<AuditEntryDto>>>();
            if (!string.IsNullOrWhiteSpace(configuration["Services:Identity:BaseUrl"]))
                auditTasks.Add(identity.AuditAsync(from, to, ct));
            if (!string.IsNullOrWhiteSpace(configuration["Services:Catalog:BaseUrl"]))
                auditTasks.Add(catalog.AuditAsync(from, to, ct));
            if (!string.IsNullOrWhiteSpace(configuration["Services:Ordering:BaseUrl"]))
                auditTasks.Add(ordering.AuditAsync(from, to, ct));
            if (!string.IsNullOrWhiteSpace(configuration["Services:Payments:BaseUrl"]))
                auditTasks.Add(payments.AuditAsync(from, to, ct));
            if (!string.IsNullOrWhiteSpace(configuration["Services:Shipping:BaseUrl"]))
                auditTasks.Add(shipping.AuditAsync(from, to, ct));

            var salesTask = !string.IsNullOrWhiteSpace(configuration["Services:Ordering:BaseUrl"])
                ? ordering.GetSalesSummaryAsync(start, end, ct)
                : Task.FromResult<SalesSummary?>(null)!;

            await Task.WhenAll(auditTasks.Cast<Task>().Append(salesTask));

            var merged = auditTasks.SelectMany(t => t.Result)
                .OrderByDescending(a => a.OccurredAt)
                .Take(100)
                .ToList();

            return Results.Ok(new BffComposedAuditReport(merged, await salesTask));
        }).RequireAuthorization(ShopPolicies.EOfficeAdmin);

        return endpoints;
    }

    private static void RegisterClient<TClient>(IServiceCollection services, string? configuredUrl, string fallbackUrl)
        where TClient : class
    {
        var baseUrl = string.IsNullOrWhiteSpace(configuredUrl) ? fallbackUrl : configuredUrl;
        services.AddHttpClient<TClient>(client => client.BaseAddress = new Uri(baseUrl))
            .AddHKShopResilienceHandler();
    }
}
