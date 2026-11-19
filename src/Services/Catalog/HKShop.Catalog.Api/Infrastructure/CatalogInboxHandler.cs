using System.Text.Json;
using HKShop.Catalog.Api.Application;
using HKShop.Catalog.Api.Domain;
using HKShop.Contracts.Events;
using HKShop.Domain.Orders;
using HKShop.Messaging;
using Microsoft.EntityFrameworkCore;

namespace HKShop.Catalog.Api.Infrastructure;

public sealed class CatalogInboxHandler(
    CatalogStockCoordinator coordinator,
    TimeProvider time) : IIntegrationEventHandler<CatalogDbContext>
{
    public async Task HandleAsync(CatalogDbContext db, EventEnvelope envelope, CancellationToken ct)
    {
        switch (envelope.Type)
        {
            case nameof(OrderPlacementRequested):
                var placement = envelope.Payload.Deserialize<OrderPlacementRequested>()!;
                await coordinator.ReserveInContextAsync(db, placement.OrderId, placement.Lines, ct);
                break;

            case nameof(OrderCancelled):
                var cancelled = envelope.Payload.Deserialize<OrderCancelled>()!;
                await CatalogStockCoordinator.ReleaseInContextAsync(db, cancelled.OrderId, null, cancelled.Reason,
                    cancelled.CancelledAt != default ? cancelled.CancelledAt : time.GetUtcNow(), ct);
                break;

            case "SellerOrderCancelled":
                await OnSellerOrderCancelledAsync(db, envelope.Payload, ct);
                break;

            case nameof(SellerOrderDispatched):
                await OnSellerOrderDispatchedAsync(db, envelope.Payload.Deserialize<SellerOrderDispatched>()!, ct);
                break;

            case nameof(SellerOrderDelivered):
                await OnSellerOrderDeliveredAsync(db, envelope.Payload.Deserialize<SellerOrderDelivered>()!, ct);
                break;

            case nameof(ReturnInspected):
                await OnReturnInspectedAsync(db, envelope.Payload.Deserialize<ReturnInspected>()!, ct);
                break;
        }
    }

    private async Task OnSellerOrderCancelledAsync(CatalogDbContext db, JsonElement payload, CancellationToken ct)
    {
        if (!payload.TryGetProperty("OrderId", out var oProp) || !oProp.TryGetGuid(out var orderId) ||
            !payload.TryGetProperty("SellerOrderId", out var soProp) || !soProp.TryGetGuid(out var sellerOrderId))
            throw new JsonException("SellerOrderCancelled requires both order identifiers.");
        var reason = payload.TryGetProperty("Reason", out var rProp) && rProp.ValueKind == JsonValueKind.String
            ? rProp.GetString() ?? "SellerOrderCancelled"
            : "SellerOrderCancelled";
        await CatalogStockCoordinator.ReleaseInContextAsync(db, orderId, sellerOrderId, reason, time.GetUtcNow(), ct);
    }

    private async Task OnSellerOrderDispatchedAsync(CatalogDbContext db, SellerOrderDispatched evt, CancellationToken ct)
    {
        var now = evt.DispatchedAt != default ? evt.DispatchedAt : time.GetUtcNow();
        var holds = await db.StockHolds
            .Where(h => h.OrderId == evt.OrderId && h.SellerId == evt.SellerId &&
                (h.SellerOrderId == null || h.SellerOrderId == evt.SellerOrderId) &&
                h.Status == ReservationStatus.Held)
            .OrderBy(h => h.InventoryBalanceId)
            .ToListAsync(ct);

        foreach (var hold in holds)
        {
            var balance = await db.InventoryBalances.FirstOrDefaultAsync(b => b.Id == hold.InventoryBalanceId, ct);
            if (balance is not null)
            {
                balance.Dispatch(hold.Quantity);
                db.OfferStockMovements.Add(new OfferStockMovement
                {
                    InventoryBalanceId = balance.Id,
                    OnHandDelta = -hold.Quantity,
                    ReservedDelta = -hold.Quantity,
                    Reason = "Order dispatched",
                    OccurredAt = now
                });
            }

            if (hold.SellerId == Seller.FirstPartyId)
            {
                var legacy = await db.StockBalances.FirstOrDefaultAsync(s => s.SkuId == hold.SkuId, ct);
                if (legacy is not null)
                {
                    legacy.Dispatch(hold.Quantity);
                    db.StockMovements.Add(new StockMovement
                    {
                        SkuId = hold.SkuId,
                        OrderId = evt.OrderId,
                        OnHandDelta = -hold.Quantity,
                        ReservedDelta = -hold.Quantity,
                        Reason = "Xuất hàng",
                        OccurredAt = now
                    });
                }
            }

            hold.Status = ReservationStatus.Consumed;
        }
    }

    private static async Task OnSellerOrderDeliveredAsync(CatalogDbContext db, SellerOrderDelivered evt, CancellationToken ct)
    {
        foreach (var productId in evt.ProductIds.Distinct())
        {
            if (await db.VerifiedPurchases.AnyAsync(v => v.SellerOrderId == evt.SellerOrderId && v.ProductId == productId, ct))
                continue;
            db.VerifiedPurchases.Add(new VerifiedPurchaseProjection
            {
                CustomerId = evt.CustomerId,
                ProductId = productId,
                SellerOrderId = evt.SellerOrderId,
                DeliveredAt = evt.DeliveredAt
            });
        }
    }

    private async Task OnReturnInspectedAsync(CatalogDbContext db, ReturnInspected evt, CancellationToken ct)
    {
        var now = evt.InspectedAt != default ? evt.InspectedAt : time.GetUtcNow();
        foreach (var line in evt.RestockedLines.Where(l => l.Quantity > 0))
        {
            var balance = await db.InventoryBalances
                .FirstOrDefaultAsync(b => b.OfferId == line.OfferId && b.SellerId == line.SellerId, ct);
            if (balance is not null)
            {
                balance.Restock(line.Quantity);
                db.OfferStockMovements.Add(new OfferStockMovement
                {
                    InventoryBalanceId = balance.Id,
                    OnHandDelta = line.Quantity,
                    ReservedDelta = 0,
                    Reason = "Return restock",
                    OccurredAt = now
                });
            }

            if (line.SellerId == Seller.FirstPartyId)
            {
                var legacy = await db.StockBalances.FirstOrDefaultAsync(s => s.SkuId == line.SkuId, ct);
                if (legacy is not null)
                {
                    legacy.Adjust(line.Quantity);
                    db.StockMovements.Add(new StockMovement
                    {
                        SkuId = line.SkuId,
                        OrderId = evt.OrderId,
                        OnHandDelta = line.Quantity,
                        ReservedDelta = 0,
                        Reason = "Return restock",
                        OccurredAt = now
                    });
                }
            }
        }
    }
}
