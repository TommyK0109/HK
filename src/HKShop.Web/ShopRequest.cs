using HKShop.Application;
using HKShop.Application.Sellers;
using HKShop.Domain;
using HKShop.Infrastructure.Persistence;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace HKShop.Web;

public sealed class ShopFailureFilter(ITempDataDictionaryFactory messages, ILogger<ShopFailureFilter> log) : IExceptionFilter
{
    public void OnException(ExceptionContext context)
    {
        if (context.Exception is SellerAccessDeniedException)
        {
            context.Result = new StatusCodeResult(StatusCodes.Status403Forbidden);
            context.ExceptionHandled = true;
            return;
        }
        if (context.Exception is not ShopRuleException error) return;
        log.LogInformation("Shop rule rejected {Action} ({TraceId}).", context.ActionDescriptor.DisplayName, context.HttpContext.TraceIdentifier);
        var http = context.HttpContext;
        if (!HttpMethods.IsPost(http.Request.Method))
        {
            context.Result = new ContentResult { StatusCode = 400, Content = error.Message, ContentType = "text/plain; charset=utf-8" };
        }
        else
        {
            messages.GetTempData(http)["Error"] = error.Message;
            var origin = http.Request.Headers.Referer.ToString();
            var fallback = http.Request.Path.StartsWithSegments("/api/eoffice") ? "/eoffice/login" : "/";
            var referer = Uri.TryCreate(origin, UriKind.Absolute, out var uri) && uri.Authority == http.Request.Host.Value
                ? uri.PathAndQuery : fallback;
            if (referer.StartsWith("//", StringComparison.Ordinal) || referer.Contains('\\')) referer = fallback;
            context.Result = new LocalRedirectResult(referer);
        }
        context.ExceptionHandled = true;
    }
}

public sealed class DatabaseReadiness(IDbContextFactory<ShopDbContext> factory, ILogger<DatabaseReadiness> log) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        if (await db.Database.CanConnectAsync(cancellationToken) &&
            !(await db.Database.GetPendingMigrationsAsync(cancellationToken)).Any()) return HealthCheckResult.Healthy();
        log.LogWarning("SQL Server or the required HKShop schema is unavailable.");
        return HealthCheckResult.Unhealthy();
    }
}

public static class AdminBootstrap
{
    public static async Task Run(IServiceProvider services, IConfiguration configuration)
    {
        var roles = services.GetRequiredService<RoleManager<IdentityRole>>();
        foreach (var role in new[] { "StoreStaff", "Admin", "Shipper" })
            if (!await roles.RoleExistsAsync(role)) Require(await roles.CreateAsync(new IdentityRole(role)));

        var email = configuration["Bootstrap:AdminEmail"]?.Trim();
        var password = configuration["Bootstrap:AdminPassword"];
        if (string.IsNullOrWhiteSpace(email) || string.IsNullOrWhiteSpace(password))
            throw new InvalidOperationException("Configure Bootstrap:AdminEmail and Bootstrap:AdminPassword for the bootstrap command.");
        var users = services.GetRequiredService<UserManager<ShopUser>>();
        var existingAdmins = await users.GetUsersInRoleAsync("Admin");
        var existing = await users.FindByEmailAsync(email);
        if (existingAdmins.Any(user => !user.Disabled))
        {
            if (existing is not null && existingAdmins.Any(user => user.Id == existing.Id && !user.Disabled)) return;
            throw new InvalidOperationException("An active administrator already exists. Use the staff invitation workflow.");
        }
        if (existing is not null)
            throw new InvalidOperationException("Choose a new email for the initial admin account.");
        var admin = new ShopUser { UserName = email, Email = email, EmailConfirmed = true, DisplayName = "Administrator" };
        Require(await users.CreateAsync(admin, password));
        Require(await users.AddToRoleAsync(admin, "Admin"));
    }

    private static void Require(IdentityResult result)
    {
        if (!result.Succeeded) throw new InvalidOperationException(string.Join(", ", result.Errors.Select(e => e.Code)));
    }
}
