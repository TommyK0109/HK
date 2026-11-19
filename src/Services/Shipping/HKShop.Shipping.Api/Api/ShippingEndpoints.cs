using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using HKShop.Application;
using HKShop.Application.Transportation;
using HKShop.Contracts.Clients;
using HKShop.Contracts.Ports;
using HKShop.Domain.Orders;
using HKShop.Messaging;
using HKShop.ServiceDefaults;
using HKShop.Shipping.Api.Application;
using HKShop.Shipping.Api.Infrastructure;
using HKShop.Web;
using Microsoft.EntityFrameworkCore;

namespace HKShop.Shipping.Api.Api;

public static class ShippingEndpoints
{
    private static readonly Regex BankReferenceRegex = new(
        @"(?<![A-Z0-9])HK[DR][0-9A-F]{32}(?![A-Z0-9])",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    public static IEndpointRouteBuilder MapShippingEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/payments/transport/bank-webhook", async (
            HttpRequest request,
            JsonElement body,
            TransportSettings settings,
            ShippingOrderLifecycle lifecycle,
            CancellationToken ct) =>
        {
            if (settings.BankWebhookKey.Length < 24 || settings.BankAccount.Length == 0 ||
                !request.Headers.TryGetValue("Authorization", out var value) || value.Count != 1 ||
                !CryptographicOperations.FixedTimeEquals(
                    Encoding.UTF8.GetBytes(value.ToString()),
                    Encoding.UTF8.GetBytes($"Apikey {settings.BankWebhookKey}")))
                return Results.Json(new { success = false }, statusCode: StatusCodes.Status401Unauthorized);

            if (body.ValueKind != JsonValueKind.Object)
                return Results.BadRequest(new { success = false });
            if (ReadString(body, "transferType") != "in" || ReadString(body, "accountNumber") != settings.BankAccount)
                return Results.Ok(new { success = true });
            if (!body.TryGetProperty("id", out var id) || id.ValueKind != JsonValueKind.Number ||
                !id.TryGetInt64(out var transaction) || transaction <= 0 ||
                !body.TryGetProperty("transferAmount", out var valueAmount) ||
                valueAmount.ValueKind != JsonValueKind.Number || !valueAmount.TryGetDecimal(out var amount) ||
                amount <= 0 || amount != decimal.Truncate(amount) || amount >= 1_000_000_000_000_000_000m)
                return Results.BadRequest(new { success = false });

            var references = BankReferenceRegex.Matches(ReadString(body, "code") + " " + ReadString(body, "content"))
                .Select(match => match.Value.ToUpperInvariant())
                .Distinct()
                .ToArray();

            await lifecycle.RecordBankReceiptAsync(
                transaction.ToString(CultureInfo.InvariantCulture),
                references.Length == 1 ? references[0] : "",
                amount,
                ct);
            return Results.Ok(new { success = true });
        }).AllowAnonymous();

        var group = app.MapGroup("/internal/shipping").RequireAuthorization();

        group.MapPost("/routes/quote", async (QuoteRoutesRequest req, ShippingOrderLifecycle lifecycle, CancellationToken ct) =>
            Results.Ok(await lifecycle.QuoteRoutesAsync(req.Destination, req.Origins, req.AcceptedRoutes, req.QuoteNewRoutes, ct)));

        group.MapPost("/parcels/register", async (RegisterParcelsRequest req, ShippingOrderLifecycle lifecycle, CancellationToken ct) =>
            Results.Ok(await lifecycle.RegisterParcelsAsync(req.Parcels, ct)));

        group.MapPost("/parcels/summaries", async (ParcelSummariesRequest req, ShippingOrderLifecycle lifecycle, CancellationToken ct) =>
            Results.Ok(await lifecycle.GetSummariesAsync(req.SellerOrderIds, req.RedactDeliveryCode, ct)));

        group.MapGet("/sellers/{sellerId:long}/location", async (
            long sellerId, IShopRequestContext req, ShippingOrderLifecycle lifecycle, CancellationToken ct) =>
        {
            var location = await lifecycle.GetSellerLocationAsync(await req.Actor(), sellerId, ct);
            return location is null ? Results.NotFound() : Results.Ok(location);
        });

        group.MapPost("/sellers/{sellerId:long}/location", async (
            long sellerId, SetSellerLocationRequest body, IShopRequestContext req, ShippingOrderLifecycle lifecycle, CancellationToken ct) =>
        {
            await lifecycle.SetSellerLocationAsync(await req.Actor(), sellerId, body.Address, body.Latitude, body.Longitude, ct);
            return Results.Ok();
        });

        group.MapPost("/parcels/{sellerOrderId:guid}/pack", async (
            Guid sellerOrderId, SellerParcelCommandRequest body, IShopRequestContext req, ShippingOrderLifecycle lifecycle, CancellationToken ct) =>
            Results.Ok(new OrderIdResult(await lifecycle.MarkPackedAsync(await req.Actor(), body.SellerId, sellerOrderId, ct))));

        group.MapPost("/parcels/{sellerOrderId:guid}/request-pickup", async (
            Guid sellerOrderId, SellerParcelCommandRequest body, IShopRequestContext req, ShippingOrderLifecycle lifecycle, CancellationToken ct) =>
            Results.Ok(new OrderIdResult(await lifecycle.RequestPickupAsync(await req.Actor(), body.SellerId, sellerOrderId, ct))));

        group.MapPost("/parcels/{sellerOrderId:guid}/confirm-handoff", async (
            Guid sellerOrderId, ConfirmHandoffRequest body, IShopRequestContext req, ShippingOrderLifecycle lifecycle, CancellationToken ct) =>
            Results.Ok(new OrderIdResult(await lifecycle.ConfirmPickupAsync(await req.Actor(), body.SellerId, sellerOrderId, body.Code, ct))));

        group.MapPost("/parcels/{sellerOrderId:guid}/cancel", async (
            Guid sellerOrderId, CancelParcelRequest body, ShippingOrderLifecycle lifecycle, CancellationToken ct) =>
        {
            await lifecycle.CancelParcelAsync(sellerOrderId, body.Now, ct);
            return Results.Ok();
        });

        group.MapGet("/parcels/{sellerOrderId:guid}/unverified-cash", async (
            Guid sellerOrderId, ShippingOrderLifecycle lifecycle, CancellationToken ct) =>
            Results.Ok(new BooleanResult(await lifecycle.HasUnverifiedCashRemittanceAsync(sellerOrderId, ct))));

        group.MapPost("/parcels/{sellerOrderId:guid}/complete-return", async (
            Guid sellerOrderId, CancelParcelRequest body, ShippingOrderLifecycle lifecycle, CancellationToken ct) =>
        {
            await lifecycle.CompleteReturnAsync(sellerOrderId, body.Now, ct);
            return Results.Ok();
        });

        group.MapPost("/shippers/configure", async (
            ConfigureShipperRequest body, IShopRequestContext req, ShippingOrderLifecycle lifecycle, CancellationToken ct) =>
        {
            await lifecycle.ConfigureShipperAsync(await req.Actor(), body.UserId, body.Email, body.Active, ct);
            return Results.Ok();
        }).RequireAuthorization(ShopPolicies.EOfficeAdmin);

        group.MapPost("/parcels/{sellerOrderId:guid}/reassign", async (
            Guid sellerOrderId, IShopRequestContext req, ShippingOrderLifecycle lifecycle, CancellationToken ct) =>
        {
            await lifecycle.ReassignAsync(await req.Actor(), sellerOrderId, ct);
            return Results.Ok();
        }).RequireAuthorization(ShopPolicies.EOfficeStaff);

        group.MapPost("/parcels/{sellerOrderId:guid}/reset-delivery-code", async (
            Guid sellerOrderId, IShopRequestContext req, ShippingOrderLifecycle lifecycle, CancellationToken ct) =>
        {
            await lifecycle.ResetDeliveryConfirmationAsync(await req.Actor(), sellerOrderId, ct);
            return Results.Ok();
        }).RequireAuthorization(ShopPolicies.EOfficeStaff);

        group.MapPost("/parcels/{sellerOrderId:guid}/payout", async (
            Guid sellerOrderId, RecordShipperPayoutRequest body, IShopRequestContext req, ShippingOrderLifecycle lifecycle, CancellationToken ct) =>
        {
            await lifecycle.RecordShipperPayoutAsync(await req.Actor(), sellerOrderId, body.Reference, ct);
            return Results.Ok();
        }).RequireAuthorization(ShopPolicies.EOfficeAdmin);

        group.MapGet("/operations", async (IShopRequestContext req, ShippingOrderLifecycle lifecycle, CancellationToken ct) =>
            Results.Ok(await lifecycle.OperationsAsync(await req.Actor(), ct)))
            .RequireAuthorization(ShopPolicies.EOfficeStaff);

        group.MapGet("/bank-exceptions", async (IShopRequestContext req, ShippingOrderLifecycle lifecycle, CancellationToken ct) =>
            Results.Ok(await lifecycle.BankExceptionsAsync(await req.Actor(), ct)))
            .RequireAuthorization(ShopPolicies.EOfficeAdmin);

        group.MapPost("/bank-exceptions/resolve", async (
            ResolveBankReceiptRequest body, IShopRequestContext req, ShippingOrderLifecycle lifecycle, CancellationToken ct) =>
        {
            await lifecycle.ResolveBankReceiptAsync(await req.Actor(), body.TransactionId, body.Reference, body.Resolution, ct);
            return Results.Ok();
        }).RequireAuthorization(ShopPolicies.EOfficeAdmin);

        group.MapGet("/shipper/state", async (IShopRequestContext req, ShippingOrderLifecycle lifecycle, CancellationToken ct) =>
            Results.Ok(await lifecycle.ShipperStateAsync(await req.Actor(), ct)))
            .RequireAuthorization(ShopPolicies.EOfficeShipper);

        group.MapPost("/shipper/presence", async (
            ShipperPresenceRequest body, IShopRequestContext req, ShippingOrderLifecycle lifecycle, CancellationToken ct) =>
        {
            await lifecycle.UpdatePresenceAsync(await req.Actor(), body.OnDuty, body.Latitude, body.Longitude, ct);
            return Results.Ok();
        }).RequireAuthorization(ShopPolicies.EOfficeShipper);

        group.MapPost("/shipper/assignments/{assignmentId:guid}/respond", async (
            Guid assignmentId, ShipperRespondRequest body, IShopRequestContext req, ShippingOrderLifecycle lifecycle, CancellationToken ct) =>
        {
            await lifecycle.RespondAsync(await req.Actor(), body.SellerOrderId, assignmentId, body.Accept, ct);
            return Results.Ok();
        }).RequireAuthorization(ShopPolicies.EOfficeShipper);

        group.MapPost("/shipper/parcels/{sellerOrderId:guid}/deliver", async (
            Guid sellerOrderId, ShipperDeliverRequest body, IShopRequestContext req, ShippingOrderLifecycle lifecycle, CancellationToken ct) =>
        {
            var ok = await lifecycle.DeliverAsync(await req.Actor(), sellerOrderId, body.Code, body.Method, body.Cash, ct);
            return Results.Ok(new BooleanResult(ok));
        }).RequireAuthorization(ShopPolicies.EOfficeShipper);

        group.MapPost("/shipper/parcels/{sellerOrderId:guid}/return", async (
            Guid sellerOrderId, ShipperReturnRequest body, IShopRequestContext req, ShippingOrderLifecycle lifecycle, CancellationToken ct) =>
        {
            await lifecycle.ReturnToSenderAsync(await req.Actor(), sellerOrderId, body.Issue, ct);
            return Results.Ok();
        }).RequireAuthorization(ShopPolicies.EOfficeShipper);

        group.MapGet("/reports/audit", async (
            DateTimeOffset from, DateTimeOffset to, IDbContextFactory<ShippingDbContext> factory, CancellationToken ct) =>
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

    private static string ReadString(JsonElement body, string name) =>
        body.TryGetProperty(name, out var prop) && prop.ValueKind == JsonValueKind.String ? prop.GetString() ?? "" : "";
}
