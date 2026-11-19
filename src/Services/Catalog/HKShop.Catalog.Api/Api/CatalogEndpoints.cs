using HKShop.Application;
using HKShop.Application.Catalog;
using HKShop.Application.Checkout;
using HKShop.Catalog.Api.Application;
using HKShop.Catalog.Api.Infrastructure;
using HKShop.Contracts.Clients;
using HKShop.Contracts.Ports;
using HKShop.Domain.Sellers;
using HKShop.Messaging;
using HKShop.Web;
using Microsoft.EntityFrameworkCore;

namespace HKShop.Catalog.Api.Api;

public static class CatalogEndpoints
{
    public static IEndpointRouteBuilder MapCatalogEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/media/{name}", async (
            string name,
            IProductImageStore media,
            HttpContext context,
            CancellationToken ct) =>
        {
            var image = await media.OpenRead(name, ct);
            if (image is null) return Results.NotFound();
            context.Response.RegisterForDisposeAsync(image);
            context.Response.Headers.CacheControl = CatalogImageUpload.ImmutableCacheControl;
            return Results.Stream(image.Content, image.ContentType);
        }).AllowAnonymous();

        var group = app.MapGroup("/internal/catalog").RequireAuthorization();

        group.MapGet("/categories", async (bool? activeOnly, CatalogStockCoordinator coordinator, CancellationToken ct) =>
            Results.Ok(await coordinator.CategoriesAsync(activeOnly ?? true, ct)))
            .AllowAnonymous();

        group.MapPost("/categories", async (
            SaveCategoryRequest body,
            IShopRequestContext req,
            CatalogStockCoordinator coordinator,
            CancellationToken ct) =>
            Results.Ok(new IntIdResult(await coordinator.SaveCategoryAsync(await req.Actor(), body.Id, body.Name, body.Active, ct))))
            .RequireAuthorization(ShopPolicies.EOfficeStaff);

        group.MapGet("/products", async (
            int? page,
            IShopRequestContext req,
            CatalogStockCoordinator coordinator,
            CancellationToken ct) =>
            Results.Ok(await coordinator.ProductsAsync(await req.Actor(), page ?? 1, ct)))
            .RequireAuthorization(ShopPolicies.EOfficeStaff);

        group.MapGet("/products/{id:int}", async (
            int id,
            IShopRequestContext req,
            CatalogStockCoordinator coordinator,
            CancellationToken ct) =>
            Results.Ok(await coordinator.EditDetailsAsync(await req.Actor(), id, ct)))
            .RequireAuthorization(ShopPolicies.EOfficeStaff);

        group.MapPost("/products", async (
            SaveProductRequest body,
            IShopRequestContext req,
            CatalogStockCoordinator coordinator,
            CancellationToken ct) =>
            Results.Ok(new IntIdResult(await coordinator.SaveProductAsync(
                await req.Actor(), body.Id, body.Name, body.Slug, body.Description, body.CategoryId, ct))))
            .RequireAuthorization(ShopPolicies.EOfficeStaff);

        group.MapPost("/skus", async (
            SaveSkuRequest body,
            IShopRequestContext req,
            CatalogStockCoordinator coordinator,
            CancellationToken ct) =>
            Results.Ok(new LongIdResult(await coordinator.SaveSkuAsync(
                await req.Actor(), body.ProductId, body.SkuId, body.Code, body.Size, body.color, body.Price, body.Active, ct))))
            .RequireAuthorization(ShopPolicies.EOfficeStaff);

        group.MapPost("/products/{id:int}/images", async (
            int id,
            HttpRequest request,
            IShopRequestContext req,
            CatalogStockCoordinator coordinator,
            CancellationToken ct) =>
        {
            var form = await request.ReadFormAsync(ct);
            var alt = form["alt"].ToString();
            var file = form.Files.GetFile("file");
            if (file is null || file.Length == 0)
                return Results.BadRequest("Image file is required.");
            await using var stream = file.OpenReadStream();
            await coordinator.AddImageAsync(await req.Actor(), id, alt, stream, ct);
            return Results.Ok();
        }).RequireAuthorization(ShopPolicies.EOfficeStaff);

        group.MapPost("/products/{id:int}/publication", async (
            int id,
            ChangePublicationRequest body,
            IShopRequestContext req,
            CatalogStockCoordinator coordinator,
            CancellationToken ct) =>
            Results.Ok(new IntIdResult(await coordinator.ChangePublicationAsync(await req.Actor(), id, body.Publish, ct))))
            .RequireAuthorization(ShopPolicies.EOfficeStaff);

        group.MapGet("/stock/first-party", async (
            bool? low,
            int? page,
            IShopRequestContext req,
            CatalogStockCoordinator coordinator,
            CancellationToken ct) =>
            Results.Ok(await coordinator.ListFirstPartyStockAsync(await req.Actor(), low ?? false, page ?? 1, ct)))
            .RequireAuthorization(ShopPolicies.EOfficeStaff);

        group.MapGet("/stock/first-party/{skuId:long}/history", async (
            long skuId,
            IShopRequestContext req,
            CatalogStockCoordinator coordinator,
            CancellationToken ct) =>
            Results.Ok(await coordinator.FirstPartyHistoryAsync(await req.Actor(), skuId, ct)))
            .RequireAuthorization(ShopPolicies.EOfficeStaff);

        group.MapPost("/stock/first-party/adjust", async (
            AdjustStockRequest body,
            IShopRequestContext req,
            CatalogStockCoordinator coordinator,
            CancellationToken ct) =>
            Results.Ok(new IntIdResult(await coordinator.AdjustFirstPartyStockAsync(
                await req.Actor(), body.SkuId, body.Delta, body.Threshold, body.Reason, ct))))
            .RequireAuthorization(ShopPolicies.EOfficeStaff);

        group.MapPost("/offers/cart", async (CartOffersRequest body, CatalogStockCoordinator coordinator,
            IDbContextFactory<CatalogDbContext> factory, CancellationToken ct) =>
        {
            IReadOnlyList<long> ids = body.OfferIds;
            if (body.FirstPartySkuId is long sku)
            {
                await using var db = await factory.CreateDbContextAsync(ct);
                ids = await db.SellerOffers.Where(o => o.SkuId == sku && o.SellerId == 1).Select(o => o.Id).ToListAsync(ct);
            }
            var offers = await coordinator.QuoteOffersAsync(ids, ct);
            return Results.Ok(new CartOffersResponse(offers.Select(o => new CartLine(
                o.OfferId, o.SkuId, o.SellerId, o.SellerName, o.SellerSkuCode, o.ProductSlug, o.ProductName,
                0, o.Available, o.PriceVnd, o.Active, false)).ToList()));
        }).AllowAnonymous();

        group.MapPost("/offers/quote", async (QuoteOffersRequest body, CatalogStockCoordinator coordinator, CancellationToken ct) =>
            Results.Ok(await coordinator.QuoteOffersAsync(body.OfferIds, ct)));

        group.MapPost("/stock/reserve", async (ReserveStockRequest body, CatalogStockCoordinator coordinator, CancellationToken ct) =>
            Results.Ok(await coordinator.ReserveOffersAsync(body.OrderId, body.Lines, ct)));

        group.MapPost("/stock/release", async (ReleaseStockRequest body, CatalogStockCoordinator coordinator, CancellationToken ct) =>
        {
            await coordinator.ReleaseOrderStockAsync(body.OrderId, body.SellerOrderId, body.Reason, ct);
            return Results.Ok();
        });

        group.MapGet("/store-applications/{id:guid}/documents/{kind}", async (
            Guid id,
            StoreDocumentKind kind,
            IShopRequestContext req,
            CatalogStockCoordinator coordinator,
            CancellationToken ct) =>
        {
            var doc = await coordinator.GetVerificationDocumentAsync(await req.Actor(), id, kind, ct);
            return doc is null ? Results.NotFound() : Results.File(doc.Data, doc.ContentType);
        }).RequireAuthorization(ShopPolicies.EOfficeAdmin);

        group.MapPost("/store-applications/{id:guid}/decision", async (
            Guid id,
            StoreDecisionRequest body,
            IShopRequestContext req,
            CatalogStockCoordinator coordinator,
            CancellationToken ct) =>
        {
            await coordinator.DecideStoreApplicationAsync(await req.Actor(), id, body.Approve, body.DeclineReason, ct);
            return Results.Ok();
        }).RequireAuthorization(ShopPolicies.EOfficeAdmin);

        group.MapPost("/sellers/{sellerId:long}/status", async (
            long sellerId,
            SellerStatusRequest body,
            IShopRequestContext req,
            CatalogStockCoordinator coordinator,
            CancellationToken ct) =>
            Results.Ok(new BooleanResult(await coordinator.SetSellerStatusAsync(
                await req.Actor(), sellerId, body.Status, body.Reason, ct))))
            .RequireAuthorization(ShopPolicies.EOfficeAdmin);

        group.MapPost("/moderation/offers/{offerId:long}", async (
            long offerId,
            OfferReviewDecisionRequest body,
            IShopRequestContext req,
            CatalogStockCoordinator coordinator,
            CancellationToken ct) =>
            Results.Ok(new BooleanResult(await coordinator.ReviewOfferAsync(await req.Actor(), offerId, body.Approve, ct))))
            .RequireAuthorization(ShopPolicies.EOfficeAdmin);

        group.MapGet("/reports/audit", async (
            DateTimeOffset from,
            DateTimeOffset to,
            IDbContextFactory<CatalogDbContext> factory,
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
