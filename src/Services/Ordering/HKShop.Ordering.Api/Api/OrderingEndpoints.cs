using HKShop.Application;
using HKShop.Application.Checkout;
using HKShop.Application.Orders;
using HKShop.Contracts.Clients;
using HKShop.Contracts.Ports;
using HKShop.Domain;
using HKShop.Domain.Orders;
using HKShop.Messaging;
using HKShop.Ordering.Api.Application;
using HKShop.Ordering.Api.Infrastructure;
using HKShop.Web;
using Microsoft.EntityFrameworkCore;

namespace HKShop.Ordering.Api.Api;

public static class OrderingEndpoints
{
    public static IEndpointRouteBuilder MapOrderingEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/internal/ordering").RequireAuthorization();

        group.MapPost("/cart/read", async (
            CartReadRequest body,
            IShopRequestContext req,
            OrderingCart cart,
            CancellationToken ct) =>
        {
            var actor = await req.Actor();
            var customer = actor.UserId.Length > 0 ? actor.UserId : null;
            return Results.Ok(await cart.ReadAsync(customer, customer is null ? body.GuestHash : null, ct));
        }).AllowAnonymous();

        group.MapPost("/cart/change", async (
            CartChangeRequest body,
            IShopRequestContext req,
            OrderingCart cart,
            CancellationToken ct) =>
        {
            var actor = await req.Actor();
            if (actor.IsEmployee)
                throw new ShopRuleException("Employee and shipper accounts cannot use shopping features.");
            var customer = actor.UserId.Length > 0 ? actor.UserId : null;
            return Results.Ok(await cart.ChangeAsync(customer, body, ct));
        }).AllowAnonymous();

        group.MapPost("/cart/merge", async (
            CartReadRequest body,
            IShopRequestContext req,
            OrderingCart cart,
            CancellationToken ct) =>
        {
            var actor = await req.Actor();
            actor.RequireCustomer();
            return Results.Ok(await cart.MergeAsync(actor.UserId, body.GuestHash ?? "", ct));
        }).RequireAuthorization(ShopPolicies.CommerceCustomer);

        group.MapPost("/cart/buy-again/{orderId:guid}", async (
            Guid orderId,
            IShopRequestContext req,
            OrderingCart cart,
            CancellationToken ct) =>
        {
            var actor = await req.Actor();
            actor.RequireCustomer();
            return Results.Ok(await cart.BuyAgainAsync(actor.UserId, orderId, ct));
        }).RequireAuthorization(ShopPolicies.CommerceCustomer);

        group.MapPost("/checkout/quote", async (
            QuoteCheckoutRequest body,
            IShopRequestContext req,
            OrderingSagaOrchestrator saga,
            CancellationToken ct) =>
            Results.Ok(await saga.QuoteAsync(await req.Actor(), body.AddressId, body.Method, body.Coupon, ct)))
            .RequireAuthorization(ShopPolicies.CommerceCustomer);

        group.MapPost("/checkout/place", async (
            PlaceOrderRequest body,
            IShopRequestContext req,
            OrderingSagaOrchestrator saga,
            CancellationToken ct) =>
            Results.Ok(await saga.StartCheckoutSagaAsync(await req.Actor(), body.Quote, ct)))
            .RequireAuthorization(ShopPolicies.CommerceCustomer);

        group.MapGet("/orders/mine", async (
            OrderStatus? status,
            int? page,
            IShopRequestContext req,
            OrderingSagaOrchestrator saga,
            CancellationToken ct) =>
            Results.Ok(await saga.ListCustomerOrdersAsync(await req.Actor(), status, page ?? 1, ct)))
            .RequireAuthorization(ShopPolicies.CommerceCustomer);

        group.MapGet("/orders", async (
            OrderStatus? status,
            int? page,
            IShopRequestContext req,
            OrderingSagaOrchestrator saga,
            CancellationToken ct) =>
            Results.Ok(await saga.ListOrdersAsync(await req.Actor(), status, page ?? 1, ct)))
            .RequireAuthorization(ShopPolicies.EOfficeStaff);

        group.MapGet("/orders/{orderId:guid}", async (
            Guid orderId,
            IShopRequestContext req,
            OrderingSagaOrchestrator saga,
            CancellationToken ct) =>
        {
            var result = await saga.GetOrderStatusAsync(await req.Actor(), orderId, ct);
            return result is null ? Results.NotFound() : Results.Ok(result);
        });

        group.MapGet("/orders/{orderId:guid}/details", async (
            Guid orderId,
            IShopRequestContext req,
            OrderingSagaOrchestrator saga,
            CancellationToken ct) =>
        {
            var result = await saga.GetOrderDetailsAsync(await req.Actor(), orderId, ct);
            return result is null ? Results.NotFound() : Results.Ok(result);
        });

        group.MapPost("/orders/{orderId:guid}/cancel", async (
            Guid orderId,
            CancelOrderBody body,
            IShopRequestContext req,
            OrderingSagaOrchestrator saga,
            CancellationToken ct) =>
        {
            await saga.CancelOrderAsync(await req.Actor(), orderId, body.Reason, ct);
            return Results.Ok();
        });

        group.MapPost("/seller-orders/{sellerOrderId:guid}/cancel", async (
            Guid sellerOrderId,
            CancelOrderBody body,
            IShopRequestContext req,
            OrderingSagaOrchestrator saga,
            CancellationToken ct) =>
        {
            await saga.CancelSellerOrderAsync(await req.Actor(), sellerOrderId, body.Reason, body.SellerId, ct);
            return Results.Ok();
        });

        group.MapPost("/seller-orders/{sellerOrderId:guid}/pack", async (
            Guid sellerOrderId,
            SellerScopeBody? body,
            IShopRequestContext req,
            OrderingSagaOrchestrator saga,
            CancellationToken ct) =>
        {
            await saga.PackSellerOrderAsync(await req.Actor(), sellerOrderId, body?.SellerId, ct);
            return Results.Ok();
        });

        group.MapPost("/seller-orders/{sellerOrderId:guid}/dispatch", async (
            Guid sellerOrderId,
            DispatchSellerOrderBody body,
            IShopRequestContext req,
            OrderingSagaOrchestrator saga,
            CancellationToken ct) =>
        {
            await saga.DispatchSellerOrderAsync(await req.Actor(), sellerOrderId, body.Carrier, body.TrackingNumber, body.SellerId, ct);
            return Results.Ok();
        });

        group.MapPost("/seller-orders/{sellerOrderId:guid}/deliver", async (
            Guid sellerOrderId,
            SellerScopeBody? body,
            IShopRequestContext req,
            OrderingSagaOrchestrator saga,
            CancellationToken ct) =>
        {
            await saga.DeliverSellerOrderAsync(await req.Actor(), sellerOrderId, body?.SellerId, ct);
            return Results.Ok();
        });

        group.MapPost("/returns/{returnId:guid}/inspect", async (
            Guid returnId,
            InspectReturnBody body,
            IShopRequestContext req,
            OrderingSagaOrchestrator saga,
            CancellationToken ct) =>
        {
            await saga.InspectReturnAsync(await req.Actor(), returnId, body.Inspection, body.Note, body.SellerId, ct);
            return Results.Ok();
        });

        group.MapGet("/reports/sales", async (
            DateOnly start,
            DateOnly end,
            IShopRequestContext req,
            OrderingSagaOrchestrator saga,
            CancellationToken ct) =>
            Results.Ok(await saga.GetSalesSummaryAsync(await req.Actor(), start, end, ct)))
            .RequireAuthorization(ShopPolicies.EOfficeAdmin);

        group.MapGet("/reports/audit", async (
            DateTimeOffset from,
            DateTimeOffset to,
            IDbContextFactory<OrderingDbContext> factory,
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
