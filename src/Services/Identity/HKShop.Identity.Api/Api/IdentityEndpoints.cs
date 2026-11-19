using HKShop.Application;
using HKShop.Contracts.Clients;
using HKShop.Contracts.Ports;
using HKShop.Identity.Api.Application;
using HKShop.Identity.Api.Infrastructure;
using HKShop.Messaging;
using HKShop.Web;
using Microsoft.EntityFrameworkCore;

namespace HKShop.Identity.Api.Api;

public static class IdentityEndpoints
{
    public static IEndpointRouteBuilder MapIdentityEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/internal/identity");

        group.MapPost("/tokens/issue", async (IssueTokenRequest body, JwtTokenIssuer issuer, CancellationToken ct) =>
            Results.Ok(await issuer.IssueTokensAsync(body, ct)))
            .AllowAnonymous();

        group.MapPost("/tokens/refresh", async (RefreshTokenRequest body, JwtTokenIssuer issuer, CancellationToken ct) =>
            Results.Ok(await issuer.RefreshAsync(body.RefreshToken, ct)))
            .AllowAnonymous();

        group.MapPost("/tokens/revoke", async (RevokeTokenRequest body, JwtTokenIssuer issuer, CancellationToken ct) =>
        {
            await issuer.RevokeAsync(body.RefreshToken, ct);
            return Results.Ok();
        }).AllowAnonymous();

        group.MapPost("/register", async (RegisterUserBody body, JwtTokenIssuer issuer, CancellationToken ct) =>
        {
            await issuer.RegisterAsync(body.Email, body.DisplayName, body.Password, ct);
            return Results.Ok();
        }).AllowAnonymous();

        group.MapGet("/addresses", async (IShopRequestContext req, JwtTokenIssuer issuer, CancellationToken ct) =>
        {
            var actor = await req.Actor();
            actor.RequireCustomer();
            return Results.Ok(await issuer.ListAddressesAsync(actor.UserId, ct));
        }).RequireAuthorization(ShopPolicies.CommerceCustomer);

        group.MapGet("/addresses/{addressId:long}", async (
            long addressId, IShopRequestContext req, JwtTokenIssuer issuer, CancellationToken ct) =>
        {
            var actor = await req.Actor();
            actor.RequireCustomer();
            var address = await issuer.GetAddressAsync(actor.UserId, addressId, ct);
            return address is null ? Results.NotFound() : Results.Ok(address);
        }).RequireAuthorization(ShopPolicies.CommerceCustomer);

        group.MapPost("/addresses", async (
            SaveAddressBody body, IShopRequestContext req, JwtTokenIssuer issuer, CancellationToken ct) =>
        {
            var actor = await req.Actor();
            actor.RequireCustomer();
            return Results.Ok(new LongIdResult(await issuer.SaveAddressAsync(
                actor.UserId, body.Id, body.Recipient, body.Phone, body.AddressText, body.Latitude, body.Longitude, ct)));
        }).RequireAuthorization(ShopPolicies.CommerceCustomer);

        group.MapDelete("/addresses/{addressId:long}", async (
            long addressId, IShopRequestContext req, JwtTokenIssuer issuer, CancellationToken ct) =>
        {
            var actor = await req.Actor();
            actor.RequireCustomer();
            await issuer.DeleteAddressAsync(actor.UserId, addressId, ct);
            return Results.Ok();
        }).RequireAuthorization(ShopPolicies.CommerceCustomer);

        group.MapPost("/users/{userId}/disabled", async (
            string userId, DisableUserBody body, IShopRequestContext req, JwtTokenIssuer issuer, CancellationToken ct) =>
        {
            await issuer.SetUserDisabledAsync(await req.Actor(), userId, body.Disabled, ct);
            return Results.Ok();
        }).RequireAuthorization(ShopPolicies.EOfficeAdmin);

        group.MapPost("/shippers", async (
            SetShipperBody body, IShopRequestContext req, JwtTokenIssuer issuer, CancellationToken ct) =>
        {
            await issuer.SetShipperRoleAsync(await req.Actor(), body.Email, body.Active, ct);
            return Results.Ok();
        }).RequireAuthorization(ShopPolicies.EOfficeAdmin);

        group.MapGet("/reports/audit", async (
            DateTimeOffset from,
            DateTimeOffset to,
            IDbContextFactory<IdentityServiceDbContext> factory,
            CancellationToken ct) =>
        {
            await using var db = await factory.CreateDbContextAsync(ct);
            var rows = await db.Set<ServiceAuditEntry>().AsNoTracking()
                .Where(a => a.OccurredAt >= from && a.OccurredAt < to)
                .OrderByDescending(a => a.Id)
                .Take(100)
                .Select(a => new AuditEntryDto(a.Id, a.SellerId, a.ActorId, a.Action, a.Target, a.Reason, a.OccurredAt))
                .ToListAsync(ct);
            return Results.Ok(rows);
        }).RequireAuthorization(ShopPolicies.EOfficeAdmin);

        return app;
    }
}
