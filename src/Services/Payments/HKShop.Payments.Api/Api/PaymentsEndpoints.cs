using System.Text.Json;
using HKShop.Application;
using HKShop.Contracts.Clients;
using HKShop.Contracts.Ports;
using HKShop.Messaging;
using HKShop.Payments.Api.Application;
using HKShop.Payments.Api.Infrastructure;
using HKShop.Web;
using Microsoft.EntityFrameworkCore;

namespace HKShop.Payments.Api.Api;

public static class PaymentsEndpoints
{
    public static IEndpointRouteBuilder MapPaymentsEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/payments/sepay/ipn", async (
            HttpRequest request,
            JsonElement body,
            SePayPaymentGateway gateway,
            PaymentsProcessor processor,
            ILoggerFactory loggerFactory,
            CancellationToken ct) =>
        {
            if (!request.Headers.TryGetValue("X-Secret-Key", out var secret) || secret.Count != 1 || !gateway.Authenticate(secret.ToString()))
                return Results.Unauthorized();
            if (body.ValueKind != JsonValueKind.Object)
                return Results.BadRequest();

            var kind = gateway.ReadString(body, "notification_type");
            if (kind == "TRANSACTION_VOID")
            {
                if (body.TryGetProperty("order", out var order))
                    await processor.FlagVoidAsync(gateway.ReadString(order, "order_invoice_number"), ct);
                return Results.Ok(new { success = true });
            }
            if (kind != "ORDER_PAID")
                return Results.Ok(new { success = true });

            var outcome = gateway.ParsePayment(body);
            if (outcome is null || !await processor.ApplyGatewayOutcomeAsync(outcome, ct))
            {
                loggerFactory.CreateLogger("HKShop.Payments.SePay")
                    .LogWarning("SePay payment notification could not be matched ({TraceId}).", request.HttpContext.TraceIdentifier);
                return Results.BadRequest(new { success = false });
            }

            return Results.Ok(new { success = true });
        }).AllowAnonymous();

        app.MapGet("/payments/sepay/return/{id:guid}", (Guid id) =>
            Results.Redirect($"/orders/{id:D}?paymentReturn=true"))
            .AllowAnonymous();

        var group = app.MapGroup("/internal/payments").RequireAuthorization();

        group.MapGet("/config", (PaymentsProcessor processor) =>
            Results.Ok(new PaymentConfigDto(processor.OnlineAvailable)))
            .AllowAnonymous();

        group.MapGet("/orders/{orderId:guid}", async (
            Guid orderId,
            IShopRequestContext req,
            PaymentsProcessor processor,
            CancellationToken ct) =>
        {
            var result = await processor.GetOrderPaymentDetailsAsync(await req.Actor(), orderId, ct);
            return result is null ? Results.NotFound() : Results.Ok(result);
        });

        group.MapGet("/orders/{orderId:guid}/checkout-form", async (
            Guid orderId,
            IShopRequestContext req,
            PaymentsProcessor processor,
            CancellationToken ct) =>
            Results.Ok(await processor.BuildCheckoutFormAsync(await req.Actor(), orderId, ct)))
            .RequireAuthorization(ShopPolicies.CommerceCustomer);

        group.MapPost("/orders/{orderId:guid}/retry", async (
            Guid orderId,
            IShopRequestContext req,
            PaymentsProcessor processor,
            CancellationToken ct) =>
            Results.Ok(new CreateRetryAttemptResponse(await processor.CreateRetryAttemptAsync(await req.Actor(), orderId, ct))))
            .RequireAuthorization(ShopPolicies.CommerceCustomer);

        group.MapGet("/issues", async (
            IShopRequestContext req,
            PaymentsProcessor processor,
            CancellationToken ct) =>
            Results.Ok(await processor.IssuesAsync(await req.Actor(), ct)))
            .RequireAuthorization(ShopPolicies.EOfficeAdmin);

        group.MapGet("/refunds/pending", async (
            IShopRequestContext req,
            PaymentsProcessor processor,
            CancellationToken ct) =>
            Results.Ok(await processor.PendingRefundsAsync(await req.Actor(), ct)))
            .RequireAuthorization(ShopPolicies.EOfficeAdmin);

        group.MapPost("/refunds/{refundId:long}/complete", async (
            long refundId,
            CompleteRefundRequest body,
            IShopRequestContext req,
            PaymentsProcessor processor,
            CancellationToken ct) =>
        {
            await processor.CompleteRefundAsync(await req.Actor(), refundId, body.Reference, ct);
            return Results.Ok();
        }).RequireAuthorization(ShopPolicies.EOfficeAdmin);

        group.MapGet("/sellers/{sellerId:long}/balances", async (
            long sellerId,
            IShopRequestContext req,
            PaymentsProcessor processor,
            CancellationToken ct) =>
            Results.Ok(await processor.GetSellerBalancesAsync(await req.Actor(), sellerId, ct)));

        group.MapGet("/payouts", async (
            int? page,
            IShopRequestContext req,
            PaymentsProcessor processor,
            CancellationToken ct) =>
            Results.Ok(await processor.GetPayoutQueueAsync(await req.Actor(), page ?? 1, ct)))
            .RequireAuthorization(ShopPolicies.EOfficeAdmin);

        group.MapPost("/sellers/{sellerId:long}/payouts", async (
            long sellerId,
            RequestPayoutRequest body,
            IShopRequestContext req,
            PaymentsProcessor processor,
            CancellationToken ct) =>
            Results.Ok(new OrderIdResult(await processor.RequestPayoutAsync(await req.Actor(), sellerId, body.SubmissionKey, body.AmountVnd, ct))));

        group.MapPost("/payouts/{payoutId:guid}/review", async (
            Guid payoutId,
            ReviewPayoutRequest body,
            IShopRequestContext req,
            PaymentsProcessor processor,
            CancellationToken ct) =>
        {
            await processor.ReviewPayoutAsync(await req.Actor(), payoutId, body.Approve, body.Note, ct);
            return Results.Ok();
        }).RequireAuthorization(ShopPolicies.EOfficeAdmin);

        group.MapGet("/reports/audit", async (
            DateTimeOffset from,
            DateTimeOffset to,
            IDbContextFactory<PaymentsDbContext> factory,
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
