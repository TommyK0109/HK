using System.Numerics;
using System.Security.Cryptography;
using System.Text.Json;
using HKShop.Application;
using HKShop.Application.Checkout;
using HKShop.Application.Orders;
using HKShop.Application.Reporting;
using HKShop.Application.Transportation;
using HKShop.Contracts.Clients;
using HKShop.Contracts.Events;
using HKShop.Contracts.Ports;
using HKShop.Domain;
using HKShop.Domain.Orders;
using HKShop.Domain.Promotions;
using HKShop.Domain.Sellers;
using HKShop.Messaging;
using HKShop.Ordering.Api.Domain;
using HKShop.Ordering.Api.Infrastructure;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;

namespace HKShop.Ordering.Api.Application;

public sealed class OrderingSagaOrchestrator(
    IDbContextFactory<OrderingDbContext> factory,
    CatalogServiceClient catalogClient,
    ShippingServiceClient shippingClient,
    IdentityServiceClient identityClient,
    ShippingClientOptions shippingOptions,
    ShopSettings settings,
    TimeProvider time)
{
    private const long FirstPartySellerId = 1;

    public async Task<CheckoutPreviewDto> QuoteAsync(
        ShopActor actor,
        long addressId,
        PaymentMethod method,
        string coupon,
        CancellationToken ct)
    {
        actor.RequireCustomer();
        coupon = ShopRules.Text(coupon, 40, "Coupon code", false).ToUpperInvariant();
        if (!Enum.IsDefined(method)) throw new ShopRuleException("Invalid payment method.");

        var address = await identityClient.GetCustomerAddressAsync(addressId, ct)
            ?? throw new ShopRuleException("The shipping address does not exist.");

        await using var db = await factory.CreateDbContextAsync(ct);
        var now = time.GetUtcNow();
        var cart = await db.ShoppingCarts.Include(c => c.Items)
            .FirstOrDefaultAsync(c => c.CustomerId == actor.UserId, ct)
            ?? throw new ShopRuleException("Your cart is empty.");
        if (cart.Items.Count == 0) throw new ShopRuleException("Your cart is empty.");

        var quotedOffers = (await catalogClient.QuoteOffersAsync(new(cart.Items.Select(i => i.OfferId).ToList()), ct))
            .ToDictionary(o => o.OfferId);

        var lines = cart.Items.Select(item =>
        {
            if (!quotedOffers.TryGetValue(item.OfferId, out var offer))
                throw new ShopRuleException("Please update quantities or remove out-of-stock items.");
            return new CartLine(offer.OfferId, offer.SkuId, offer.SellerId, offer.SellerName, offer.SellerSkuCode,
                offer.ProductSlug, offer.ProductName, item.Quantity, offer.Available, offer.PriceVnd, offer.Active,
                item.SeenUnitPriceVnd != null && item.SeenUnitPriceVnd != offer.PriceVnd);
        }).ToList();

        if (lines.Any(l => !l.Active || l.Quantity > l.Available))
            throw new ShopRuleException("Please update quantities or remove out-of-stock items.");

        var supportsCod = lines.All(l => l.SellerId == FirstPartySellerId);
        if (method == PaymentMethod.Cod && !supportsCod)
            throw new ShopRuleException("Cash on Delivery is only available for single-seller HKShop orders. Prepaid SePay is required for third-party or multi-seller carts.");
        if ((shippingOptions.Enabled && method == PaymentMethod.Cod) ||
            (!shippingOptions.Enabled && method == PaymentMethod.TransportAtDelivery))
            throw new ShopRuleException("Select a payment method available for this checkout.");

        var subtotal = ShopRules.Vnd(lines.Sum(l => l.Price * l.Quantity));
        var (promotion, discount) = await EvaluatePromotionAsync(db, coupon, actor.UserId, subtotal, now, ct);
        var flatShipping = shippingOptions.Enabled ? 0m : ShopRules.Vnd(settings.FlatShippingVnd);
        var commissionBps = ShopRules.CommissionBps(settings.DefaultCommissionBps);
        var sellers = Allocate(lines, discount, flatShipping, commissionBps);

        IReadOnlyList<TransportRoute>? routes = null;
        if (shippingOptions.Enabled)
        {
            var dest = TransportPoint.Require(address.Latitude, address.Longitude, "your delivery address");
            var origins = lines.GroupBy(l => l.SellerId).Select(g =>
            {
                var first = quotedOffers[g.First().OfferId];
                if (first.LocationId is null || string.IsNullOrWhiteSpace(first.PickupAddress))
                    throw new ShopRuleException("A seller does not have a confirmed storage location for this parcel.");
                return new SellerRouteOrigin(g.Key, first.LocationId.Value, first.PickupAddress, first.PickupLatitude, first.PickupLongitude);
            }).ToList();

            routes = await shippingClient.QuoteRoutesAsync(new(dest, origins, null, QuoteNewRoutes: true), ct);
            for (var i = 0; i < sellers.Count; i++)
                sellers[i] = sellers[i] with { ShippingVnd = routes.Single(r => r.SellerId == sellers[i].SellerId).FeeVnd };
        }

        var shippingFee = ShopRules.Vnd(sellers.Sum(s => s.ShippingVnd));
        var quote = new CheckoutQuote(actor.UserId, addressId, method, coupon, cart.Revision, Guid.NewGuid(),
            now.AddMinutes(10), Fingerprint(address, method, promotion?.Id, discount, shippingFee, sellers))
        {
            TransportRoutes = routes
        };

        return new CheckoutPreviewDto(quote, address, lines, sellers, subtotal, discount, shippingFee);
    }

    public async Task<PlaceOrderResponse> StartCheckoutSagaAsync(ShopActor actor, CheckoutQuote quote, CancellationToken ct)
    {
        actor.RequireCustomer();
        if (quote.CustomerId != actor.UserId || quote.SubmissionKey == Guid.Empty)
            throw new ShopRuleException("Invalid checkout session.");
        if (!Enum.IsDefined(quote.Method))
            throw new ShopRuleException("Invalid payment method.");

        var now = time.GetUtcNow();
        ShoppingCart cart;
        await using (var readDb = await factory.CreateDbContextAsync(ct))
        {
            var existing = await readDb.Orders.AsNoTracking()
                .FirstOrDefaultAsync(o => o.CustomerId == actor.UserId && o.SubmissionKey == quote.SubmissionKey, ct);
            if (existing is not null)
                return new PlaceOrderResponse(existing.Id, existing.Status);

            if (now >= quote.ExpiresAt)
                throw new ShopRuleException("Checkout quote has expired. Please review your order again.");

            cart = await readDb.ShoppingCarts.AsNoTracking().Include(c => c.Items)
                .FirstOrDefaultAsync(c => c.CustomerId == actor.UserId, ct)
                ?? throw new ShopRuleException("Your cart is empty.");
        }
        if (cart.Revision != quote.Revision)
            throw new ShopRuleException("Your cart changed. Please review your order again.");
        if (shippingOptions.Enabled != (quote.TransportRoutes is not null))
            throw new ShopRuleException("Checkout quote has expired. Please review your order again.");

        var address = await identityClient.GetCustomerAddressAsync(quote.AddressId, ct)
            ?? throw new ShopRuleException("The shipping address does not exist.");

        var quotedOffers = (await catalogClient.QuoteOffersAsync(new(cart.Items.Select(i => i.OfferId).ToList()), ct))
            .ToDictionary(o => o.OfferId);
        var lines = cart.Items.Select(item =>
        {
            if (!quotedOffers.TryGetValue(item.OfferId, out var offer))
                throw new ShopRuleException("Please update quantities or remove out-of-stock items.");
            return new CartLine(offer.OfferId, offer.SkuId, offer.SellerId, offer.SellerName, offer.SellerSkuCode,
                offer.ProductSlug, offer.ProductName, item.Quantity, offer.Available, offer.PriceVnd, offer.Active,
                item.SeenUnitPriceVnd != null && item.SeenUnitPriceVnd != offer.PriceVnd);
        }).ToList();

        if (lines.Count == 0 || lines.Any(l => !l.Active || l.Quantity > l.Available))
            throw new ShopRuleException("Please update quantities or remove out-of-stock items.");

        var subtotal = ShopRules.Vnd(lines.Sum(l => l.Price * l.Quantity));
        var flatShipping = shippingOptions.Enabled ? 0m : ShopRules.Vnd(settings.FlatShippingVnd);
        var commissionBps = ShopRules.CommissionBps(settings.DefaultCommissionBps);

        IReadOnlyList<TransportRoute>? verifiedRoutes = null;
        if (shippingOptions.Enabled)
        {
            var dest = TransportPoint.Require(address.Latitude, address.Longitude, "your delivery address");
            var origins = lines.GroupBy(l => l.SellerId).Select(g =>
            {
                var first = quotedOffers[g.First().OfferId];
                if (first.LocationId is null || string.IsNullOrWhiteSpace(first.PickupAddress))
                    throw new ShopRuleException("A seller does not have a confirmed storage location for this parcel.");
                return new SellerRouteOrigin(g.Key, first.LocationId.Value, first.PickupAddress, first.PickupLatitude, first.PickupLongitude);
            }).ToList();
            verifiedRoutes = await shippingClient.QuoteRoutesAsync(new(dest, origins, quote.TransportRoutes, QuoteNewRoutes: false), ct);
        }

        try
        {
            return await SerializableTransaction.ExecuteAsync(factory, async db =>
            {
                var existing = await db.Orders.AsNoTracking()
                    .FirstOrDefaultAsync(o => o.CustomerId == actor.UserId && o.SubmissionKey == quote.SubmissionKey, ct);
                if (existing is not null)
                    return new PlaceOrderResponse(existing.Id, existing.Status);

                var currentRevision = await db.ShoppingCarts.AsNoTracking()
                    .Where(c => c.CustomerId == actor.UserId).Select(c => (Guid?)c.Revision).FirstOrDefaultAsync(ct);
                if (currentRevision != quote.Revision)
                    throw new ShopRuleException("Your cart changed. Please review your order again.");

                var (promotion, discount) = await EvaluatePromotionAsync(db, quote.Coupon, actor.UserId, subtotal, now, ct);
                var sellers = Allocate(lines, discount, flatShipping, commissionBps);
                if (verifiedRoutes is not null)
                {
                    for (var i = 0; i < sellers.Count; i++)
                        sellers[i] = sellers[i] with { ShippingVnd = verifiedRoutes.Single(r => r.SellerId == sellers[i].SellerId).FeeVnd };
                }

                var shippingFee = ShopRules.Vnd(sellers.Sum(s => s.ShippingVnd));
                if (Fingerprint(address, quote.Method, promotion?.Id, discount, shippingFee, sellers) != quote.Fingerprint)
                    throw new ShopRuleException("Pricing, items, or delivery address changed. Please review your order again.");

                var total = ShopRules.Vnd(subtotal - discount + shippingFee);
                var supportsCod = sellers.Count == 1 && sellers[0].SellerId == FirstPartySellerId;
                if (!shippingOptions.Enabled && quote.Method == PaymentMethod.SePay && total == 0 && supportsCod)
                    throw new ShopRuleException("Orders totaling 0 VND do not require SePay. Select Cash on Delivery.");
                var requiresOnlineCapture = quote.Method == PaymentMethod.SePay && total > 0;
                var order = new Order
                {
                    SellerId = sellers.Count == 1 ? sellers[0].SellerId : null,
                    CustomerId = actor.UserId,
                    CustomerEmail = actor.Email,
                    SubmissionKey = quote.SubmissionKey,
                    CreatedAt = now,
                    PaymentDeadline = requiresOnlineCapture ? now.AddMinutes(15) : null,
                    PaymentMethod = quote.Method,
                    PaymentStatus = requiresOnlineCapture
                        ? PaymentStatus.Pending
                        : (quote.Method != PaymentMethod.SePay ? PaymentStatus.Due : PaymentStatus.Paid),
                    Recipient = address.Recipient,
                    Phone = address.Phone,
                    ShippingAddress = address.AddressText,
                    ShippingVnd = shippingFee,
                    TotalVnd = total,
                    QuotedCartRevision = cart.Revision,
                    QuotedCartSnapshotJson = JsonSerializer.Serialize(quote.TransportRoutes)
                };
                order.Number = $"HK{order.Id:N}".ToUpperInvariant();
                order.ChangeStatus(OrderStatus.AwaitingPayment, now, "Pending stock reservation");

                for (var i = 0; i < sellers.Count; i++)
                {
                    var sq = sellers[i];
                    var sellerOrder = new SellerOrder
                    {
                        OrderId = order.Id,
                        SellerId = sq.SellerId,
                        Number = $"{order.Number}-S{i + 1}",
                        SubtotalVnd = sq.SubtotalVnd,
                        SellerDiscountVnd = sq.SellerDiscountVnd,
                        PlatformDiscountVnd = sq.PlatformDiscountVnd,
                        ShippingVnd = sq.ShippingVnd,
                        TotalVnd = sq.TotalVnd,
                        CommissionVnd = sq.CommissionVnd,
                        CreatedAt = now,
                        Status = OrderStatus.AwaitingPayment
                    };
                    order.SellerOrders.Add(sellerOrder);

                    foreach (var line in sq.Lines)
                    {
                        var offer = quotedOffers[line.OfferId];
                        order.Items.Add(new OrderItem
                        {
                            OrderId = order.Id,
                            SellerOrderId = sellerOrder.Id,
                            ProductId = offer.ProductId,
                            SellerId = line.SellerId,
                            SkuId = line.SkuId,
                            OfferId = line.OfferId,
                            SkuCode = offer.SkuCode,
                            SellerSkuCode = offer.SellerSkuCode,
                            Name = line.Name,
                            ProductAndVariantSnapshot = offer.ProductAndVariantSnapshot,
                            Quantity = line.Quantity,
                            UnitPriceVnd = line.Price,
                            DiscountVnd = line.SellerDiscountVnd + line.PlatformDiscountVnd,
                            SellerDiscountVnd = line.SellerDiscountVnd,
                            PlatformDiscountVnd = line.PlatformDiscountVnd,
                            CommissionBps = line.CommissionBps,
                            CommissionVnd = line.CommissionVnd
                        });
                    }
                }

                db.Orders.Add(order);
                if (promotion is not null)
                {
                    db.PromotionClaims.Add(new PromotionClaim
                    {
                        PromotionId = promotion.Id,
                        OrderId = order.Id,
                        CustomerId = actor.UserId,
                        Status = ClaimStatus.Held
                    });
                }

                var reserveLines = order.Items.Select(item => new StockReservationLine(item.OfferId, item.SkuId,
                    item.SellerId, item.Quantity, item.UnitPriceVnd, item.SellerOrderId)).ToList();
                db.EnqueueOutboxEvent("Order", order.Id.ToString("D"), nameof(OrderPlacementRequested),
                    new OrderPlacementRequested(order.Id, order.Number, actor.UserId, reserveLines, now),
                    $"OrderPlacementRequested:{order.Id:N}", now);

                return new PlaceOrderResponse(order.Id, order.Status);
            }, ct);
        }
        catch (DbUpdateException ex) when (ex.InnerException is SqlException { Number: 2601 or 2627 })
        {
            await using var lookupDb = await factory.CreateDbContextAsync(ct);
            var existing = await lookupDb.Orders.AsNoTracking()
                .FirstOrDefaultAsync(o => o.CustomerId == actor.UserId && o.SubmissionKey == quote.SubmissionKey, ct);
            if (existing is not null)
                return new PlaceOrderResponse(existing.Id, existing.Status);
            if (!string.IsNullOrWhiteSpace(quote.Coupon))
                throw new ShopRuleException("The discount code has reached its usage limit.");
            throw;
        }
    }

    public async Task<OrderStatusResponse?> GetOrderStatusAsync(ShopActor actor, Guid orderId, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var order = await db.Orders.AsNoTracking()
            .FirstOrDefaultAsync(o => o.Id == orderId && (actor.Staff || actor.Admin || o.CustomerId == actor.UserId), ct);
        return order is null
            ? null
            : new OrderStatusResponse(order.Id, order.Number, order.Status, order.PaymentStatus, order.TotalVnd);
    }

    public async Task<OrderDetailsResponse?> GetOrderDetailsAsync(ShopActor actor, Guid orderId, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var order = await db.Orders.AsNoTracking()
            .Include(o => o.SellerOrders).ThenInclude(s => s.Shipment)
            .Include(o => o.Items)
            .Include(o => o.Events)
            .FirstOrDefaultAsync(o => o.Id == orderId && (actor.Staff || actor.Admin || o.CustomerId == actor.UserId), ct);
        if (order is null) return null;

        var returns = await db.ReturnRequests.AsNoTracking()
            .Include(r => r.Items)
            .Where(r => r.OrderId == orderId)
            .OrderByDescending(r => r.RequestedAt)
            .ToListAsync(ct);

        return new OrderDetailsResponse(
            order.Id,
            order.SellerId,
            order.Number,
            order.CustomerId,
            order.CustomerEmail,
            order.CreatedAt,
            order.DeliveredAt,
            order.PaymentDeadline,
            order.Status,
            order.PaymentMethod,
            order.PaymentStatus,
            order.Recipient,
            order.Phone,
            order.ShippingAddress,
            order.ShippingVnd,
            order.TotalVnd,
            order.SellerOrders.OrderBy(s => s.SellerId).Select(so => new EOfficeSellerOrderDto(
                so.Id, so.OrderId, so.SellerId, so.Number, so.Status, so.SubtotalVnd, so.SellerDiscountVnd,
                so.PlatformDiscountVnd, so.ShippingVnd, so.TotalVnd, so.CommissionVnd, so.CreatedAt, so.DeliveredAt,
                so.Shipment is null ? null : new EOfficeShipmentDto(so.Shipment.SellerOrderId, so.Shipment.OrderId, so.Shipment.Carrier, so.Shipment.TrackingNumber, so.Shipment.DispatchedAt),
                null)).ToList(),
            order.Items.OrderBy(i => i.Id).Select(i => new EOfficeOrderItemDto(
                i.Id, i.OrderId, i.SellerOrderId, i.SellerId, i.SkuId, i.OfferId, i.SkuCode, i.SellerSkuCode, i.Name,
                i.Quantity, i.UnitPriceVnd, i.DiscountVnd)).ToList(),
            order.Events.OrderBy(e => e.Id).Select(e => new EOfficeOrderEventDto(e.Id, e.Status, e.OccurredAt, e.Note)).ToList(),
            returns.Select(r => new EOfficeReturnDto(
                r.Id, r.OrderId, r.SellerOrderId, r.Status, r.Reason, r.StaffNote, r.RequestedAt,
                r.Items.Select(i => new EOfficeReturnItemDto(i.Id, i.ReturnId, i.SellerOrderId, i.OrderItemId, i.Requested, i.Accepted, i.Restocked)).ToList())).ToList());
    }

    public async Task<Page<EOfficeOrderSummaryDto>> ListCustomerOrdersAsync(ShopActor actor, OrderStatus? status, int page, CancellationToken ct)
    {
        actor.RequireCustomer();
        await using var db = await factory.CreateDbContextAsync(ct);
        var query = db.Orders.AsNoTracking().Where(o => o.CustomerId == actor.UserId && (status == null || o.Status == status));
        var count = await query.CountAsync(ct);
        var number = Math.Clamp(page, 1, Math.Max(1, (int)Math.Ceiling(count / 25d)));
        var items = await query.OrderByDescending(o => o.CreatedAt).ThenByDescending(o => o.Id)
            .Skip((number - 1) * 25).Take(25)
            .Select(o => new EOfficeOrderSummaryDto(o.Id, o.Number, o.CreatedAt, o.Status, o.TotalVnd))
            .ToListAsync(ct);
        return new Page<EOfficeOrderSummaryDto>(items, number, count, 25);
    }

    public async Task<Page<EOfficeOrderSummaryDto>> ListOrdersAsync(ShopActor actor, OrderStatus? status, int page, CancellationToken ct)
    {
        actor.RequireStaff();
        await using var db = await factory.CreateDbContextAsync(ct);
        var query = db.Orders.AsNoTracking().Where(o => status == null || o.Status == status);
        var count = await query.CountAsync(ct);
        var number = Math.Clamp(page, 1, Math.Max(1, (int)Math.Ceiling(count / 25d)));
        var items = await query.OrderByDescending(o => o.CreatedAt).ThenByDescending(o => o.Id)
            .Skip((number - 1) * 25).Take(25)
            .Select(o => new EOfficeOrderSummaryDto(o.Id, o.Number, o.CreatedAt, o.Status, o.TotalVnd))
            .ToListAsync(ct);
        return new Page<EOfficeOrderSummaryDto>(items, number, count, 25);
    }

    public async Task CancelOrderAsync(ShopActor actor, Guid orderId, string reason, CancellationToken ct)
    {
        if (!actor.Staff && !actor.Admin) actor.RequireCustomer();
        reason = ShopRules.Text(reason, 250, "Cancellation reason", false);

        await using var db = await factory.CreateDbContextAsync(ct);
        var order = await db.Orders.Include(o => o.SellerOrders).Include(o => o.Items)
            .FirstOrDefaultAsync(o => o.Id == orderId && (actor.Staff || actor.Admin || o.CustomerId == actor.UserId), ct)
            ?? throw new ShopRuleException("Order not found.");
        if (order.Status == OrderStatus.Cancelled) return;
        order.RequireStatus(OrderStatus.AwaitingPayment, OrderStatus.Confirmed);
        if (order.SellerOrders.Any(s => s.Status is not (OrderStatus.AwaitingPayment or OrderStatus.Confirmed or OrderStatus.Cancelled)))
            throw new ShopRuleException("A seller has already started fulfilling this order.");

        var now = time.GetUtcNow();
        foreach (var child in order.SellerOrders.Where(s => s.Status != OrderStatus.Cancelled))
        {
            child.ChangeStatus(OrderStatus.Cancelled, now);
            QueueFullSellerRefund(db, order, child, "cancel", now);
        }

        var claim = await db.PromotionClaims.FirstOrDefaultAsync(c => c.OrderId == order.Id, ct);
        if (claim is not null) claim.Status = ClaimStatus.Released;

        order.ChangeStatus(OrderStatus.Cancelled, now, reason);
        db.EnqueueOutboxEvent("Order", order.Id.ToString("D"), nameof(OrderCancelled),
            new OrderCancelled(order.Id, order.Number, order.CustomerEmail, reason,
                order.SellerOrders.Select(s => s.Id).ToList(), now),
            $"OrderCancelled:{order.Id:N}", now);

        await db.SaveChangesAsync(ct);
    }

    public async Task CancelSellerOrderAsync(ShopActor actor, Guid sellerOrderId, string reason, long? sellerId, CancellationToken ct)
    {
        reason = ShopRules.Text(reason, 250, "Cancellation reason", false);
        await using var db = await factory.CreateDbContextAsync(ct);
        var (order, child) = await FindSellerOrderForFulfillmentAsync(db, actor, sellerOrderId, sellerId, ct);
        if (child.Status == OrderStatus.Cancelled) return;
        if (child.Status is not (OrderStatus.Confirmed or OrderStatus.Packing))
            throw new ShopRuleException("The current seller order status does not allow this action.");

        var now = time.GetUtcNow();
        child.ChangeStatus(OrderStatus.Cancelled, now);
        QueueFullSellerRefund(db, order, child, "cancel", now);
        await UpdateParentSummaryAsync(db, order, now, ct);
        db.AddAuditEntry(actor.UserId, "Hủy đơn", child.Number, now, reason, child.SellerId);
        db.EnqueueOutboxEvent("SellerOrder", child.Id.ToString("D"), "SellerOrderCancelled",
            new { SellerOrderId = child.Id, OrderId = order.Id, child.SellerId, child.Number, Reason = reason, CancelledAt = now },
            $"SellerOrder:{child.Id:N}:Cancelled", now);
        await db.SaveChangesAsync(ct);
    }

    public async Task PackSellerOrderAsync(ShopActor actor, Guid sellerOrderId, long? sellerId, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var (order, child) = await FindSellerOrderForFulfillmentAsync(db, actor, sellerOrderId, sellerId, ct);
        if (child.Status != OrderStatus.Confirmed)
            throw new ShopRuleException("The current seller order status does not allow this action.");

        var now = time.GetUtcNow();
        child.ChangeStatus(OrderStatus.Packing, now);
        await UpdateParentSummaryAsync(db, order, now, ct);
        db.AddAuditEntry(actor.UserId, "Đóng gói", child.Number, now, sellerId: child.SellerId);
        db.EnqueueOutboxEvent("SellerOrder", child.Id.ToString("D"), nameof(SellerOrderPacked),
            new SellerOrderPacked(child.Id, order.Id, child.SellerId, child.Number, now),
            $"SellerOrderPacked:{child.Id:N}", now);
        await db.SaveChangesAsync(ct);
    }

    public async Task DispatchSellerOrderAsync(ShopActor actor, Guid sellerOrderId, string carrier, string tracking, long? sellerId, CancellationToken ct)
    {
        carrier = ShopRules.Text(carrier, 100, "Carrier");
        tracking = ShopRules.Text(tracking, 100, "Tracking number");
        await using var db = await factory.CreateDbContextAsync(ct);
        var (order, child) = await FindSellerOrderForFulfillmentAsync(db, actor, sellerOrderId, sellerId, ct);
        if (child.Status != OrderStatus.Packing)
            throw new ShopRuleException("The current seller order status does not allow this action.");

        var now = time.GetUtcNow();
        db.Shipments.Add(new Shipment
        {
            OrderId = order.Id,
            SellerOrderId = child.Id,
            Carrier = carrier,
            TrackingNumber = tracking,
            DispatchedAt = now
        });
        child.ChangeStatus(OrderStatus.Shipped, now);
        await UpdateParentSummaryAsync(db, order, now, ct);
        var lines = order.Items.Where(i => i.SellerOrderId == child.Id)
            .Select(i => new StockReservationLine(i.OfferId, i.SkuId, i.SellerId, i.Quantity, i.UnitPriceVnd)).ToList();

        db.AddAuditEntry(actor.UserId, "Xuất hàng", child.Number, now, tracking, child.SellerId);
        db.EnqueueOutboxEvent("SellerOrder", child.Id.ToString("D"), nameof(SellerOrderDispatched),
            new SellerOrderDispatched(child.Id, order.Id, child.SellerId, child.Number, order.CustomerEmail, carrier, tracking, lines, now),
            $"SellerOrderDispatched:{child.Id:N}", now);
        await db.SaveChangesAsync(ct);
    }

    public async Task DeliverSellerOrderAsync(ShopActor actor, Guid sellerOrderId, long? sellerId, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var (order, child) = await FindSellerOrderForFulfillmentAsync(db, actor, sellerOrderId, sellerId, ct);
        if (child.Status != OrderStatus.Shipped)
            throw new ShopRuleException("The current seller order status does not allow this action.");

        var now = time.GetUtcNow();
        child.ChangeStatus(OrderStatus.Delivered, now);
        child.DeliveredAt = now;
        if (order.PaymentMethod == PaymentMethod.Cod)
            order.PaymentStatus = PaymentStatus.Paid;
        await UpdateParentSummaryAsync(db, order, now, ct);

        var productIds = order.Items.Where(i => i.SellerOrderId == child.Id).Select(i => i.ProductId).Where(p => p > 0).Distinct().ToList();
        db.AddAuditEntry(actor.UserId, "Xác nhận giao hàng", child.Number, now, sellerId: child.SellerId);
        db.EnqueueOutboxEvent("SellerOrder", child.Id.ToString("D"), nameof(SellerOrderDelivered),
            new SellerOrderDelivered(child.Id, order.Id, child.SellerId, child.Number, order.CustomerId, order.CustomerEmail, productIds, now),
            $"SellerOrderDelivered:{child.Id:N}", now);
        await db.SaveChangesAsync(ct);
    }

    public async Task InspectReturnAsync(
        ShopActor actor, Guid returnId, IReadOnlyList<InspectedItem> inspection, string note, long? sellerId, CancellationToken ct)
    {
        if (sellerId is null) actor.RequireStaff();
        note = ShopRules.Text(note, 250, "Inspection note");

        await using var db = await factory.CreateDbContextAsync(ct);
        var request = await db.ReturnRequests.Include(r => r.Items).FirstOrDefaultAsync(r => r.Id == returnId, ct)
            ?? throw new ShopRuleException("The return request was not found.");
        if (request.Status != ReturnStatus.Approved)
            throw new ShopRuleException("The request has not been approved or has already been inspected.");

        var order = await db.Orders.Include(o => o.SellerOrders).Include(o => o.Items)
            .FirstOrDefaultAsync(o => o.Id == request.OrderId, ct)
            ?? throw new ShopRuleException("Order not found.");
        var child = order.SellerOrders.First(s => s.Id == request.SellerOrderId);
        if (sellerId is long scope)
            await RequireSellerFulfillmentAccessAsync(db, actor, scope, ct);

        var results = inspection.ToDictionary(i => i.ItemId);
        var now = time.GetUtcNow();
        var allocations = new List<RefundLineAllocationDto>();
        var restocked = new List<StockReservationLine>();

        foreach (var line in request.Items)
        {
            if (!results.TryGetValue(line.Id, out var res) || res.Accepted < 0 || res.Accepted > line.Requested ||
                res.Restocked < 0 || res.Restocked > res.Accepted)
                throw new ShopRuleException("The inspected quantity is invalid.");

            var orderItem = order.Items.First(i => i.Id == line.OrderItemId);
            var priorAccepted = await db.ReturnItems.Where(i => i.OrderItemId == orderItem.Id && i.ReturnId != returnId)
                .SumAsync(i => i.Accepted, ct);
            if (priorAccepted + res.Accepted > orderItem.Quantity)
                throw new ShopRuleException("The return quantity exceeds the quantity purchased.");

            line.Accepted = res.Accepted;
            line.Restocked = res.Restocked;

            if (res.Accepted > 0)
            {
                var refundAmount = orderItem.RefundFor(priorAccepted, res.Accepted);
                var (prevDisc, prevComm) = ReversedAmounts(orderItem, priorAccepted);
                var (nextDisc, nextComm) = ReversedAmounts(orderItem, priorAccepted + res.Accepted);
                allocations.Add(new RefundLineAllocationDto(orderItem.Id, res.Accepted, refundAmount,
                    nextComm - prevComm, nextDisc - prevDisc));
            }
            if (res.Restocked > 0)
            {
                restocked.Add(new StockReservationLine(orderItem.OfferId, orderItem.SkuId, orderItem.SellerId,
                    res.Restocked, orderItem.UnitPriceVnd));
            }
        }

        var totalRefund = allocations.Sum(a => a.AmountVnd);
        if (totalRefund > 0)
        {
            db.EnqueueOutboxEvent("Refund", $"{order.Id:N}:return:{returnId:N}", nameof(RefundRequested),
                new RefundRequested(order.Id, child.Id, child.SellerId, returnId, $"{order.Id:N}:return:{returnId:N}",
                    totalRefund, 0m, allocations, now),
                $"RefundRequested:return:{returnId:N}", now);
            request.Status = ReturnStatus.AwaitingRefund;
        }
        else
        {
            request.Status = ReturnStatus.Completed;
        }
        request.StaffNote = note;

        if (restocked.Count > 0)
        {
            db.EnqueueOutboxEvent("ReturnRequest", returnId.ToString("D"), nameof(ReturnInspected),
                new ReturnInspected(returnId, order.Id, child.Id, child.SellerId, restocked, now),
                $"ReturnInspected:{returnId:N}", now);
        }

        db.AddAuditEntry(actor.UserId, "Kiểm hàng trả", returnId.ToString(), now, note, child.SellerId);
        await db.SaveChangesAsync(ct);
    }

    public async Task<SalesSummary> GetSalesSummaryAsync(ShopActor actor, DateOnly start, DateOnly end, CancellationToken ct)
    {
        actor.RequireAdmin();
        if (end < start || start == DateOnly.MinValue || end == DateOnly.MaxValue || end.DayNumber - start.DayNumber > 366)
            throw new ShopRuleException("The report date range is invalid.");

        var zone = TimeSpan.FromHours(7);
        var from = new DateTimeOffset(start.ToDateTime(TimeOnly.MinValue), zone);
        var to = new DateTimeOffset(end.AddDays(1).ToDateTime(TimeOnly.MinValue), zone);

        await using var db = await factory.CreateDbContextAsync(ct);
        var paidTotal = await db.Orders.AsNoTracking()
            .Where(o => o.CreatedAt >= from && o.CreatedAt < to && o.PaymentStatus == PaymentStatus.Paid)
            .SumAsync(o => o.TotalVnd, ct);
        var groups = await db.Orders.AsNoTracking()
            .Where(o => o.CreatedAt >= from && o.CreatedAt < to)
            .GroupBy(o => o.Status)
            .Select(g => new StatusCount(g.Key, g.Count()))
            .ToListAsync(ct);

        return new SalesSummary(paidTotal, 0m, groups);
    }

    public static void QueueFullSellerRefund(OrderingDbContext db, Order order, SellerOrder child, string reason, DateTimeOffset now)
    {
        if (order.PaymentMethod != PaymentMethod.TransportAtDelivery && order.PaymentStatus != PaymentStatus.Paid)
            return;

        var items = order.Items.Where(i => i.SellerOrderId == child.Id).ToList();
        var allocations = items.Select(i =>
        {
            var (disc, comm) = ReversedAmounts(i, i.Quantity);
            return new RefundLineAllocationDto(i.Id, i.Quantity, i.RefundFor(0, i.Quantity), comm, disc);
        }).ToList();

        var amount = allocations.Sum(a => a.AmountVnd) + child.ShippingVnd;
        if (amount <= 0) return;

        var key = $"{order.Id:N}:{reason}:{child.Id:N}";
        db.EnqueueOutboxEvent("Refund", key, nameof(RefundRequested),
            new RefundRequested(order.Id, child.Id, child.SellerId, null, key, amount, child.ShippingVnd, allocations, now),
            $"RefundRequested:{key}", now);
    }

    public static async Task UpdateParentSummaryAsync(OrderingDbContext db, Order order, DateTimeOffset now, CancellationToken ct)
    {
        var children = order.SellerOrders.Count > 0
            ? order.SellerOrders
            : await db.SellerOrders.Where(s => s.OrderId == order.Id).ToListAsync(ct);

        var remaining = children.Where(s => s.Status is not (OrderStatus.Cancelled or OrderStatus.Undeliverable)).ToList();
        var summary = remaining.Count > 0
            ? remaining.Min(s => s.Status)
            : children.Any(s => s.Status == OrderStatus.Undeliverable) ? OrderStatus.Undeliverable : OrderStatus.Cancelled;

        if (summary == OrderStatus.Cancelled)
        {
            var claim = await db.PromotionClaims.FirstOrDefaultAsync(c => c.OrderId == order.Id, ct);
            if (claim is not null) claim.Status = ClaimStatus.Released;
        }
        if (summary != order.Status)
            order.ChangeStatus(summary, now);
        if (summary == OrderStatus.Delivered)
            order.DeliveredAt = children.Max(s => s.DeliveredAt);
    }

    private static (decimal PlatformDiscount, decimal Commission) ReversedAmounts(OrderItem item, int quantity)
    {
        var discount = decimal.Floor(item.PlatformDiscountVnd * quantity / item.Quantity);
        var gross = item.RefundFor(0, item.Quantity) + item.PlatformDiscountVnd;
        if (gross == 0) return (discount, 0);
        var reversedGross = item.RefundFor(0, quantity) + discount;
        var commission = (decimal)((BigInteger)item.CommissionVnd * (BigInteger)reversedGross / (BigInteger)gross);
        return (discount, commission);
    }

    private async Task<(Order Order, SellerOrder Child)> FindSellerOrderForFulfillmentAsync(
        OrderingDbContext db, ShopActor actor, Guid sellerOrderId, long? sellerId, CancellationToken ct)
    {
        if (sellerId is null) actor.RequireStaff();
        else await RequireSellerFulfillmentAccessAsync(db, actor, sellerId.Value, ct);

        var child = await db.SellerOrders.FirstOrDefaultAsync(s => s.Id == sellerOrderId && (sellerId == null || s.SellerId == sellerId), ct)
            ?? throw new ShopRuleException("Seller order not found.");
        var order = await db.Orders.Include(o => o.SellerOrders).Include(o => o.Items)
            .FirstOrDefaultAsync(o => o.Id == child.OrderId, ct)
            ?? throw new ShopRuleException("Order not found.");
        return (order, child);
    }

    private static async Task RequireSellerFulfillmentAccessAsync(OrderingDbContext db, ShopActor actor, long sellerId, CancellationToken ct)
    {
        if (actor.Admin || actor.Staff) return;
        if (string.IsNullOrEmpty(actor.UserId) || !actor.Verified || !actor.Mfa)
            throw new ShopRuleException("You don't have access to this seller.");
        var member = await db.SellerMemberships.AsNoTracking()
            .FirstOrDefaultAsync(m => m.SellerId == sellerId && m.UserId == actor.UserId && m.Status == SellerMembershipStatus.Active, ct);
        if (member is null || member.Role is not (SellerRole.Owner or SellerRole.Fulfillment))
            throw new ShopRuleException("You don't have access to this seller.");
    }

    private static async Task<(Promotion? Promotion, decimal Discount)> EvaluatePromotionAsync(
        OrderingDbContext db, string code, string customerId, decimal subtotal, DateTimeOffset now, CancellationToken ct)
    {
        if (code.Length == 0) return (null, 0);
        var row = await db.Promotions.FirstOrDefaultAsync(p => p.Code == code, ct)
            ?? throw new ShopRuleException("The discount code does not exist.");
        var activeClaims = db.PromotionClaims.Where(c => c.PromotionId == row.Id && c.Status != ClaimStatus.Released);
        if (await activeClaims.CountAsync(ct) >= row.UsageCap ||
            await activeClaims.AnyAsync(c => c.CustomerId == customerId, ct))
            throw new ShopRuleException("The discount code has reached its usage limit.");
        return (row, row.Discount(subtotal, now));
    }

    private static List<SellerOrderQuote> Allocate(IReadOnlyList<CartLine> lines, decimal platformDiscount, decimal flatShippingVnd, int defaultCommissionBps) =>
        CheckoutServiceAllocator.Allocate(lines, platformDiscount, flatShippingVnd, defaultCommissionBps);

    private static string Fingerprint(CustomerAddressDto address, PaymentMethod method, long? promotion, decimal discount,
        decimal shipping, List<SellerOrderQuote> sellers) =>
        Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new
        {
            address.Recipient,
            address.Phone,
            address.AddressText,
            method,
            promotion,
            discount,
            shipping,
            Sellers = sellers.Select(s => new
            {
                s.SellerId, s.SubtotalVnd, s.PlatformDiscountVnd, s.ShippingVnd, s.CommissionVnd,
                Lines = s.Lines.Select(l => new
                {
                    l.OfferId, l.SkuId, l.Code, l.Name, l.Quantity, l.Price, l.PlatformDiscountVnd, l.CommissionBps, l.CommissionVnd
                })
            })
        })));
}

internal static class CheckoutServiceAllocator
{
    public static List<SellerOrderQuote> Allocate(IReadOnlyList<CartLine> lines, decimal platformDiscount, decimal flatShippingVnd, int defaultCommissionBps)
    {
        ShopRules.Vnd(platformDiscount);
        ShopRules.Vnd(flatShippingVnd);
        ShopRules.CommissionBps(defaultCommissionBps);

        var ordered = lines.OrderBy(l => l.SellerId).ThenBy(l => l.SkuId).ThenBy(l => l.OfferId).ToList();
        var subtotal = ordered.Sum(l => l.Price * l.Quantity);
        if (platformDiscount > subtotal) throw new ShopRuleException("Discount exceeds order subtotal.");

        var platformAllocations = new decimal[ordered.Count];
        var remainingDiscount = platformDiscount;
        for (var i = 0; i < ordered.Count; i++)
        {
            var lineTotal = ordered[i].Price * ordered[i].Quantity;
            var share = subtotal == 0 ? 0 : decimal.Floor(platformDiscount * lineTotal / subtotal);
            platformAllocations[i] = share;
            remainingDiscount -= share;
        }
        for (var i = 0; i < ordered.Count && remainingDiscount > 0; i++)
        {
            var lineTotal = ordered[i].Price * ordered[i].Quantity;
            var extra = Math.Min(remainingDiscount, lineTotal - platformAllocations[i]);
            platformAllocations[i] += extra;
            remainingDiscount -= extra;
        }

        var indexedBySeller = ordered
            .Select((line, index) => (Line: line, LineSubtotal: line.Price * line.Quantity, PlatformDiscount: platformAllocations[index]))
            .GroupBy(entry => new { entry.Line.SellerId, entry.Line.SellerName })
            .OrderBy(group => group.Key.SellerId);

        var sellers = new List<SellerOrderQuote>();
        foreach (var group in indexedBySeller)
        {
            var groupEntries = group.ToList();
            var sellerSubtotal = ShopRules.Vnd(groupEntries.Sum(e => e.LineSubtotal));
            var sellerPlatformDiscount = ShopRules.Vnd(groupEntries.Sum(e => e.PlatformDiscount));
            var bps = group.Key.SellerId == 1 ? 0 : defaultCommissionBps;
            var sellerCommission = ShopRules.Vnd(decimal.Floor(sellerSubtotal * bps / 10_000m));

            var lineCommissions = new decimal[groupEntries.Count];
            var remainingCommission = sellerCommission;
            for (var i = 0; i < groupEntries.Count; i++)
            {
                var lc = decimal.Floor(groupEntries[i].LineSubtotal * bps / 10_000m);
                lineCommissions[i] = lc;
                remainingCommission -= lc;
            }
            for (var i = 0; i < groupEntries.Count && remainingCommission > 0; i++)
            {
                var extra = Math.Min(remainingCommission, groupEntries[i].LineSubtotal - lineCommissions[i]);
                lineCommissions[i] += extra;
                remainingCommission -= extra;
            }

            var quoteLines = groupEntries.Select((entry, i) => new CheckoutLineQuote(
                entry.Line.OfferId, entry.Line.SkuId, entry.Line.SellerId, entry.Line.SellerName,
                entry.Line.Code, entry.Line.Slug, entry.Line.Name, entry.Line.Quantity, entry.Line.Available,
                entry.Line.Price, entry.LineSubtotal, 0, entry.PlatformDiscount, bps, lineCommissions[i])).ToList();

            sellers.Add(new SellerOrderQuote(
                group.Key.SellerId, group.Key.SellerName, sellerSubtotal, 0,
                sellerPlatformDiscount, flatShippingVnd, sellerCommission, quoteLines));
        }

        return sellers;
    }
}
