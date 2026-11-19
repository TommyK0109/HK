using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using HKShop.Application;
using HKShop.Web;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.IdentityModel.Tokens;

namespace HKShop.ServiceDefaults;

public sealed class JwtSettings
{
    public const string SectionName = "Jwt";
    public string Issuer { get; set; } = "https://hkshop.identity";
    public string Audience { get; set; } = "hkshop.services";
    public string SigningKey { get; set; } = "";
    public int AccessTokenMinutes { get; set; } = 10;
}

public static class ShopActorFactory
{
    public static ShopActor FromClaims(ClaimsPrincipal? principal)
    {
        if (principal?.Identity?.IsAuthenticated != true)
            return new("", "", false, false, false, false);

        if (HasBoolClaim(principal, "disabled"))
            return new("", "", false, false, false, false);

        var userId = FindClaim(principal, "sub", ClaimTypes.NameIdentifier) ?? "";
        if (userId.Length == 0)
            return new("", "", false, false, false, false);

        var email = FindClaim(principal, "email", ClaimTypes.Email, ClaimTypes.Name) ?? "";
        var verified = HasBoolClaim(principal, "email_verified") || HasBoolClaim(principal, "email_confirmed");
        var mfa = verified && (HasClaimValue(principal, "amr", "mfa") ||
                               HasClaimValue(principal, ClaimTypes.AuthenticationMethod, "mfa"));

        var isStaffRole = HasRole(principal, "StoreStaff");
        var isAdminRole = HasRole(principal, "Admin");
        var isShipperRole = HasRole(principal, "Shipper");
        var hasEmployeeRole = isStaffRole || isAdminRole || isShipperRole ||
                              HasBoolClaim(principal, "employee") || HasBoolClaim(principal, "has_employee_role");

        return new ShopActor(
            userId,
            email,
            verified,
            Staff: mfa && isStaffRole,
            Admin: mfa && isAdminRole,
            Shipper: mfa && isShipperRole)
        {
            Mfa = mfa,
            HasEmployeeRole = hasEmployeeRole
        };
    }

    public static IReadOnlyList<Claim> ToClaims(ShopActor actor)
    {
        if (string.IsNullOrWhiteSpace(actor.UserId))
            return [];

        var claims = new List<Claim>
        {
            new("sub", actor.UserId),
            new(ClaimTypes.NameIdentifier, actor.UserId),
            new("email", actor.Email),
            new("email_verified", actor.Verified ? "true" : "false")
        };

        if (actor.Mfa)
            claims.Add(new Claim("amr", "mfa"));

        if (actor.Staff)
            claims.Add(new Claim("role", "StoreStaff"));
        if (actor.Admin)
            claims.Add(new Claim("role", "Admin"));
        if (actor.Shipper)
            claims.Add(new Claim("role", "Shipper"));
        if (actor.HasEmployeeRole)
            claims.Add(new Claim("employee", "true"));

        return claims;
    }

    public static string CreateJwtToken(
        ShopActor actor,
        JwtSettings settings,
        DateTimeOffset? now = null,
        TimeSpan? lifetime = null,
        IEnumerable<Claim>? extraClaims = null)
    {
        if (string.IsNullOrWhiteSpace(settings.SigningKey) || Encoding.UTF8.GetByteCount(settings.SigningKey) < 32)
            throw new InvalidOperationException("Configure a Jwt:SigningKey of at least 32 bytes before issuing tokens.");

        var issuedAt = (now ?? DateTimeOffset.UtcNow).UtcDateTime;
        var expires = issuedAt.Add(lifetime ?? TimeSpan.FromMinutes(Math.Max(1, settings.AccessTokenMinutes)));
        var claims = ToClaims(actor).ToList();
        if (extraClaims is not null)
            claims.AddRange(extraClaims);

        var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(settings.SigningKey));
        var credentials = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);
        var token = new JwtSecurityToken(
            issuer: settings.Issuer,
            audience: settings.Audience,
            claims: claims,
            notBefore: issuedAt,
            expires: expires,
            signingCredentials: credentials);
        return new JwtSecurityTokenHandler().WriteToken(token);
    }

    internal static bool HasRole(ClaimsPrincipal principal, string role) =>
        principal.IsInRole(role) ||
        HasClaimValue(principal, "role", role) ||
        HasClaimValue(principal, ClaimTypes.Role, role);

    private static string? FindClaim(ClaimsPrincipal principal, params string[] types)
    {
        foreach (var type in types)
        {
            var value = principal.FindFirstValue(type);
            if (!string.IsNullOrEmpty(value))
                return value;
        }
        return null;
    }

    private static bool HasBoolClaim(ClaimsPrincipal principal, string type)
    {
        foreach (var claim in principal.FindAll(type))
        {
            if (bool.TryParse(claim.Value, out var flag) && flag)
                return true;
        }
        return false;
    }

    private static bool HasClaimValue(ClaimsPrincipal principal, string type, string expected) =>
        principal.FindAll(type).Any(c => string.Equals(c.Value, expected, StringComparison.OrdinalIgnoreCase));
}

public sealed class ClaimsShopUserResolver : IShopUserResolver
{
    public string? GetUserId(ClaimsPrincipal principal)
    {
        if (principal.Identity?.IsAuthenticated != true)
            return null;
        return principal.FindFirstValue("sub") ?? principal.FindFirstValue(ClaimTypes.NameIdentifier);
    }

    public Task<ShopUserIdentity?> GetIdentityAsync(ClaimsPrincipal principal)
    {
        var actor = ShopActorFactory.FromClaims(principal);
        if (actor.UserId.Length == 0)
            return Task.FromResult<ShopUserIdentity?>(null);

        var displayName = principal.FindFirstValue("name") ?? principal.FindFirstValue(ClaimTypes.Name) ?? actor.Email;
        var phone = principal.FindFirstValue("phone_number") ?? principal.FindFirstValue(ClaimTypes.MobilePhone);
        return Task.FromResult<ShopUserIdentity?>(new ShopUserIdentity(
            actor.UserId,
            actor.Email.Length > 0 ? actor.Email : null,
            displayName,
            phone,
            actor.Verified,
            actor.Mfa,
            Disabled: false));
    }

    public Task<ShopActor> LoadActorAsync(ClaimsPrincipal principal) =>
        Task.FromResult(ShopActorFactory.FromClaims(principal));
}

public static class HKShopSecurityExtensions
{
    public static IServiceCollection AddHKShopSecurity(this IServiceCollection services, IConfiguration configuration)
    {
        var jwt = configuration.GetSection(JwtSettings.SectionName).Get<JwtSettings>()
                  ?? configuration.GetSection("Authentication:Jwt").Get<JwtSettings>()
                  ?? new JwtSettings();
        services.AddSingleton(jwt);

        services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
            .AddJwtBearer(JwtBearerDefaults.AuthenticationScheme, options =>
            {
                options.MapInboundClaims = false;
                options.TokenValidationParameters = new TokenValidationParameters
                {
                    ValidateIssuer = true,
                    ValidIssuer = jwt.Issuer,
                    ValidateAudience = true,
                    ValidAudience = jwt.Audience,
                    ValidateLifetime = true,
                    ClockSkew = TimeSpan.FromSeconds(30),
                    NameClaimType = "sub",
                    RoleClaimType = "role",
                    ValidateIssuerSigningKey = !string.IsNullOrWhiteSpace(jwt.SigningKey),
                    IssuerSigningKey = !string.IsNullOrWhiteSpace(jwt.SigningKey) && Encoding.UTF8.GetByteCount(jwt.SigningKey) >= 16
                        ? new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwt.SigningKey))
                        : null
                };
            });

        services.AddHttpContextAccessor();
        services.TryAddScoped<IShopUserResolver, ClaimsShopUserResolver>();
        services.TryAddScoped<ShopRequest>();
        services.TryAddScoped<IShopRequestContext>(sp => sp.GetRequiredService<ShopRequest>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IAuthorizationHandler, ShopPlatformAuthorizationHandler>());

        services.AddAuthorization(options =>
        {
            options.AddPolicy(ShopPolicies.CommerceCustomer, policy => policy
                .RequireAuthenticatedUser()
                .AddRequirements(new ShopPlatformRequirement(actor => actor.UserId.Length > 0 && !actor.IsEmployee)));
            options.AddPolicy(ShopPolicies.CommerceSeller, policy => policy
                .RequireAuthenticatedUser()
                .RequireClaim("amr", "mfa")
                .AddRequirements(new ShopPlatformRequirement(actor => actor.UserId.Length > 0 && actor.Mfa && !actor.IsEmployee)));
            options.AddPolicy(ShopPolicies.EOfficeAny, policy => policy
                .RequireAuthenticatedUser()
                .AddRequirements(new ShopPlatformRequirement(actor => actor.UserId.Length > 0 && actor.HasEmployeeRole)));
            options.AddPolicy(ShopPolicies.EOfficeStaff, policy => policy
                .RequireAssertion(ctx => ShopActorFactory.HasRole(ctx.User, "StoreStaff") || ShopActorFactory.HasRole(ctx.User, "Admin"))
                .RequireClaim("amr", "mfa")
                .AddRequirements(new ShopPlatformRequirement(actor => actor.Mfa && (actor.Staff || actor.Admin))));
            options.AddPolicy(ShopPolicies.EOfficeAdmin, policy => policy
                .RequireAssertion(ctx => ShopActorFactory.HasRole(ctx.User, "Admin"))
                .RequireClaim("amr", "mfa")
                .AddRequirements(new ShopPlatformRequirement(actor => actor.Mfa && actor.Admin)));
            options.AddPolicy(ShopPolicies.EOfficeShipper, policy => policy
                .RequireAuthenticatedUser()
                .RequireClaim("amr", "mfa")
                .AddRequirements(new ShopPlatformRequirement(actor => actor.Mfa && actor.Shipper)));

            options.AddPolicy("Staff", policy => policy
                .RequireAssertion(ctx => ShopActorFactory.HasRole(ctx.User, "StoreStaff") || ShopActorFactory.HasRole(ctx.User, "Admin"))
                .RequireClaim("amr", "mfa"));
            options.AddPolicy("Admin", policy => policy
                .RequireAssertion(ctx => ShopActorFactory.HasRole(ctx.User, "Admin"))
                .RequireClaim("amr", "mfa"));
            options.AddPolicy("SellerMfa", policy => policy
                .RequireAuthenticatedUser()
                .RequireClaim("amr", "mfa"));
        });

        return services;
    }
}

public sealed record ShopPlatformRequirement(Func<ShopActor, bool> Predicate) : IAuthorizationRequirement;

public sealed class ShopPlatformAuthorizationHandler(IHttpContextAccessor accessor) : AuthorizationHandler<ShopPlatformRequirement>
{
    protected override async Task HandleRequirementAsync(AuthorizationHandlerContext context, ShopPlatformRequirement requirement)
    {
        var httpContext = context.Resource as HttpContext ?? accessor.HttpContext;
        if (httpContext is not null && httpContext.RequestServices.GetService<IShopRequestContext>() is { } req)
        {
            var actor = await req.Actor();
            if (requirement.Predicate(actor))
                context.Succeed(requirement);
            return;
        }

        if (requirement.Predicate(ShopActorFactory.FromClaims(context.User)))
            context.Succeed(requirement);
    }
}
