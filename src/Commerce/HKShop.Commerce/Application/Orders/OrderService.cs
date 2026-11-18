using HKShop.Application.Catalog;
using HKShop.Application.Payments;
using HKShop.Application.Sellers;
using HKShop.Application.Settlement;
using HKShop.Contracts.Ports;
using HKShop.Domain;
using HKShop.Domain.Orders;
using HKShop.Domain.Promotions;
using HKShop.Domain.Sellers;

namespace HKShop.Application.Orders;

public sealed record OrderDetails(
    Order Order,
    IReadOnlyList<ReturnRequest> Returns,
    IReadOnlyList<Refund> Refunds,
    IReadOnlyDictionary<long, string> SellerNames);
public sealed record SellerOrderListRow(SellerOrder SellerOrder, string OrderNumber);
public sealed record SellerOrderDetails(SellerOrder SellerOrder, string OrderNumber, string Recipient, string Phone,
    string ShippingAddress, PaymentMethod PaymentMethod, IReadOnlyList<OrderItem> Items);

public sealed class OrderService(IShopStore store, TimeProvider time, IShippingGateway? shipping = null)
{
    public Task<Page<Order>> List(ShopActor actor, bool adminView, OrderStatus? status, int page)
    {
        if (adminView) actor.RequireStaff();
        else actor.RequireCustomer();
        return store.Read(async db =>
        {
            var query = db.Query<Order>().Where(o => (adminView || o.CustomerId == actor.UserId)
                && (status == null || o.Status == status));
            var count = await db.Count(query);
            var number = Math.Clamp(page, 1, Math.Max(1, (int)Math.Ceiling(count / 25d)));
            return new Page<Order>(await db.List(query.OrderByDescending(o => o.CreatedAt)
                .ThenByDescending(o => o.Id).Skip((number - 1) * 25).Take(25)), number, count, 25);
        });
    }

    public Task<OrderDetails?> Details(ShopActor actor, Guid id, bool adminView = false)
    {
        if (adminView) actor.RequireStaff();
        return store.Read(async db =>
        {
            var order = await db.First(db.Query<Order>().Where(o => o.Id == id && (adminView || o.CustomerId == actor.UserId)));
            if (order is null) return null;
            order.SellerOrders = await db.List(db.Query<SellerOrder>().Where(s => s.OrderId == id).OrderBy(s => s.SellerId));
            order.Items = await db.List(db.Query<OrderItem>().Where(i => i.OrderId == id).OrderBy(i => i.Id));
            order.Events = await db.List(db.Query<OrderEvent>().Where(e => e.OrderId == id).OrderBy(e => e.Id));
            order.PaymentAttempts = await db.List(db.Query<PaymentAttempt>().Where(a => a.OrderId == id).OrderBy(a => a.AttemptNumber));
            order.Payment = await FindPayment(db, id);
            var shipments = (await db.List(db.Query<Shipment>().Where(s => s.OrderId == id)))
                .ToDictionary(s => s.SellerOrderId);
            foreach (var child in order.SellerOrders) child.Shipment = shipments.GetValueOrDefault(child.Id);
            var childIds = order.SellerOrders.Select(s => s.Id).ToArray();
            var parcels = shipping is null || childIds.Length == 0
                ? new Dictionary<Guid, TransportParcelSummary>()
                : await shipping.GetParcelSummariesAsync(db, childIds, redactDeliveryCode: adminView);
            foreach (var child in order.SellerOrders) child.Transport = parcels.GetValueOrDefault(child.Id);
            if (order.SellerOrders.Count == 1) order.Shipment = order.SellerOrders[0].Shipment;
            var sellerIds = order.SellerOrders.Select(s => s.SellerId).Distinct().ToList();
            var sellerNames = sellerIds.Count == 0
                ? new Dictionary<long, string>()
                : (await db.List(db.Query<Seller>().Where(s => sellerIds.Contains(s.Id)).Select(s => new { s.Id, s.DisplayName })))
                    .ToDictionary(s => s.Id, s => s.DisplayName);
            var returns = await db.List(db.Query<ReturnRequest>().Where(r => r.OrderId == id).OrderByDescending(r => r.RequestedAt));
            var returnIds = returns.Select(r => r.Id).ToArray();
            var items = await db.List(db.Query<ReturnItem>().Where(i => returnIds.Contains(i.ReturnId)));
            foreach (var row in returns) row.Items = items.Where(i => i.ReturnId == row.Id).ToList();
            return new OrderDetails(order, returns, await db.List(db.Query<Refund>().Where(r => r.OrderId == id).OrderBy(r => r.RequestedAt)), sellerNames);
        });
    }

    public Task<Page<SellerOrderListRow>> SellerOrders(ShopActor actor, long sellerId, OrderStatus? status, int page) =>
        store.Write(async db =>
        {
            await SellerAccess.Require(db, actor, sellerId, SellerPermission.Fulfillment);
            const int size = 25;
            var query = from child in db.Query<SellerOrder>()
                        join order in db.Query<Order>() on child.OrderId equals order.Id
                        where child.SellerId == sellerId && (status == null || child.Status == status)
                        orderby child.CreatedAt descending, child.Id descending
                        select new SellerOrderListRow(child, order.Number);
            var count = await db.Count(query);
            var number = Math.Clamp(page, 1, Math.Max(1, (int)Math.Ceiling(count / (double)size)));
            return new Page<SellerOrderListRow>(await db.List(query.Skip((number - 1) * size).Take(size)), number, count, size);
        });

    public Task<SellerOrderDetails?> SellerDetails(ShopActor actor, long sellerId, Guid sellerOrderId) =>
        store.Write<SellerOrderDetails?>(async db =>
        {
            await SellerAccess.Require(db, actor, sellerId, SellerPermission.Fulfillment);
            var child = await db.First(db.Query<SellerOrder>().Where(s => s.Id == sellerOrderId && s.SellerId == sellerId));
            if (child is null) return null;

            var order = await FindOrder(db, child.OrderId);
            var payment = await FindPayment(db, order.Id);
            var items = await db.List(db.Query<OrderItem>().Where(i => i.SellerOrderId == child.Id).OrderBy(i => i.Id));
            child.Shipment = await db.First(db.Query<Shipment>().Where(s => s.SellerOrderId == child.Id));
            child.Transport = shipping is null ? null : (await shipping.GetParcelSummariesAsync(db, [child.Id])).GetValueOrDefault(child.Id);
            var showAddress = child.Status is OrderStatus.Confirmed or OrderStatus.Packing or OrderStatus.Shipped;
            return new SellerOrderDetails(child, order.Number, showAddress ? order.Recipient : "", showAddress ? order.Phone : "",
                showAddress ? order.ShippingAddress : "", payment.Method, items);
        });

    public Task<int> Cancel(ShopActor actor, Guid id, string reason, bool adminView = false)
    {
        if (adminView) actor.RequireStaff();
        else actor.RequireCustomer();
        reason = ShopRules.Text(reason, 250, "Cancellation reason", false);
        return store.Write(async db =>
        {
            var order = await FindOrder(db, id, adminView ? null : actor.UserId);
            if (order.Status == OrderStatus.Cancelled) return 0;
            order.RequireStatus(OrderStatus.AwaitingPayment, OrderStatus.Confirmed);
            var children = await FindSellerOrders(db, id);
            if (children.Any(s => s.Status is not (OrderStatus.AwaitingPayment or OrderStatus.Confirmed or OrderStatus.Cancelled)))
                throw new ShopRuleException("A seller has already started fulfilling this order.");

            var now = time.GetUtcNow();
            await Release(db, order, now, actor.UserId);
            foreach (var child in children.Where(s => s.Status != OrderStatus.Cancelled))
            {
                await CloseTransport(db, child, now, shipping);
                MarkCancelled(db, child, now, "OrderCancelled");
                if (await QueueFullSellerRefund(db, order, child, "cancel", now))
                    await db.Save();
            }

            await CancelUnpaidPayment(db, order);
            order.ChangeStatus(OrderStatus.Cancelled, now, reason);
            db.Notify(order, "cancelled", "Order cancelled", now);
            if (adminView) db.Audit(actor.UserId, "Hủy đơn", order.Number, now, reason, order.SellerId);
            return 0;
        });
    }

    public Task<int> StartPacking(ShopActor actor, Guid id) =>
        ApplyToOnlySellerOrder(actor, id, (db, order, child, now) => Pack(db, actor, order, child, now));

    public Task<Guid> StartPackingSellerOrder(ShopActor actor, Guid sellerOrderId, long? sellerId = null) =>
        UpdateFulfillment(actor, sellerOrderId, sellerId, (db, order, child, now) => Pack(db, actor, order, child, now));

    public Task<Guid> CancelSellerOrder(ShopActor actor, Guid sellerOrderId, string reason, long? sellerId = null)
    {
        reason = ShopRules.Text(reason, 250, "Cancellation reason", false);
        return UpdateFulfillment(actor, sellerOrderId, sellerId, (db, order, child, now) =>
            CancelChild(db, order, child, now, actor.UserId, reason, shipping));
    }

    public Task<int> Dispatch(ShopActor actor, Guid id, string carrier, string tracking) =>
        ApplyToOnlySellerOrder(actor, id, (db, order, child, now) => Dispatch(db, actor, order, child, carrier, tracking, now, shipping: shipping));

    public Task<Guid> DispatchSellerOrder(ShopActor actor, Guid sellerOrderId, string carrier, string tracking, long? sellerId = null) =>
        UpdateFulfillment(actor, sellerOrderId, sellerId,
            (db, order, child, now) => Dispatch(db, actor, order, child, carrier, tracking, now, shipping: shipping));

    public Task<int> Deliver(ShopActor actor, Guid id, decimal? collected, string reference) =>
        ApplyToOnlySellerOrder(actor, id, (db, order, child, now) => Deliver(db, actor, order, child, collected, reference, now, shipping: shipping));

    public Task<Guid> DeliverSellerOrder(ShopActor actor, Guid sellerOrderId, decimal? collected, string reference, long? sellerId = null) =>
        UpdateFulfillment(actor, sellerOrderId, sellerId,
            (db, order, child, now) => Deliver(db, actor, order, child, collected, reference, now, shipping: shipping));

    public Task<int> DeliveryException(ShopActor actor, Guid id, string note) =>
        ApplyToOnlySellerOrder(actor, id, (db, order, child, now) => RecordDeliveryException(db, actor, order, child, note, now));

    public Task<Guid> DeliveryExceptionSellerOrder(ShopActor actor, Guid sellerOrderId, string note, long? sellerId = null) =>
        UpdateFulfillment(actor, sellerOrderId, sellerId,
            (db, order, child, now) => RecordDeliveryException(db, actor, order, child, note, now));

    public Task<int> ReceiveUndelivered(ShopActor actor, Guid id, string reason, IReadOnlyList<ReturnQuantity> quantities) =>
        ApplyToOnlySellerOrder(actor, id, (db, order, child, now) => ReceiveUndelivered(db, actor, order, child, reason, quantities, now, shipping));

    public Task<Guid> ReceiveUndeliveredSellerOrder(ShopActor actor, Guid sellerOrderId, string reason,
        IReadOnlyList<ReturnQuantity> quantities, long? sellerId = null) =>
        UpdateFulfillment(actor, sellerOrderId, sellerId,
            (db, order, child, now) => ReceiveUndelivered(db, actor, order, child, reason, quantities, now, shipping));

    private async Task<int> ApplyToOnlySellerOrder(ShopActor actor, Guid orderId,
        Func<IShopSession, Order, SellerOrder, DateTimeOffset, Task> action)
    {
        await UpdateFulfillment(actor, orderId, null, action, parentId: true);
        return 0;
    }

    private Task<Guid> UpdateFulfillment(ShopActor actor, Guid id, long? sellerId,
        Func<IShopSession, Order, SellerOrder, DateTimeOffset, Task> action, bool parentId = false)
    {
        if (sellerId is null) actor.RequireStaff();
        return store.Write(async db =>
        {
            SellerOrder child;
            List<SellerOrder> children;
            if (parentId)
            {
                children = await FindSellerOrders(db, id);
                if (children.Count != 1)
                    throw new ShopRuleException("Select a seller order for this fulfillment action.");
                child = children[0];
            }
            else
            {
                if (sellerId is long scope)
                    await SellerAccess.Require(db, actor, scope, SellerPermission.Fulfillment);
                child = await db.First(db.Query<SellerOrder>().Where(s => s.Id == id && (sellerId == null || s.SellerId == sellerId)))
                    ?? throw new ShopRuleException("Seller order not found.");
                children = await FindSellerOrders(db, child.OrderId);
            }

            var order = await FindOrder(db, child.OrderId);
            var now = time.GetUtcNow();
            await action(db, order, child, now);
            await UpdateSummary(db, order, children, now);
            return order.Id;
        });
    }

    internal static async Task UpdateSummary(IShopSession db, Order order, List<SellerOrder> children, DateTimeOffset now)
    {
        var summary = FulfillmentStatus(children);
        await UpdatePaymentStatus(db, order, await FindPayment(db, order.Id), children, now);
        if (summary == OrderStatus.Cancelled)
            await SetClaim(db, order.Id, ClaimStatus.Released);
        if (summary is OrderStatus.Cancelled or OrderStatus.Undeliverable)
            await CancelUnpaidPayment(db, order);
        if (summary != order.Status) order.ChangeStatus(summary, now);
        if (summary == OrderStatus.Delivered) order.DeliveredAt = children.Max(s => s.DeliveredAt);
    }

    internal static async Task UpdatePaymentStatus(IShopSession db, Order order, Payment payment,
        IReadOnlyList<SellerOrder> children, DateTimeOffset now, Guid? newlyFunded = null)
    {
        if (payment.Method != PaymentMethod.TransportAtDelivery || payment.Status == PaymentStatus.Paid) return;
        var captured = (await db.List(db.Query<PaymentAttempt>().Where(a => a.OrderId == order.Id &&
            a.AppliedToOrder && a.Status == PaymentStatus.Paid).Select(a => a.SellerOrderId))).ToHashSet();
        if (newlyFunded is not null) captured.Add(newlyFunded);
        if (captured.Count == 0 || children.Any(s => s.Status is not (OrderStatus.Cancelled or OrderStatus.Undeliverable) &&
            !captured.Contains(s.Id))) return;
        payment.Status = PaymentStatus.Paid;
        payment.PaidAt = now;
        order.PaymentStatus = PaymentStatus.Paid;
    }

    internal static async Task CancelChild(IShopSession db, Order order, SellerOrder child, DateTimeOffset now,
        string? actorId, string reason, IShippingGateway? shipping = null)
    {
        if (child.Status == OrderStatus.Cancelled) return;
        RequireStatus(child, OrderStatus.Confirmed, OrderStatus.Packing);
        await CloseTransport(db, child, now, shipping);
        await MoveHeld(db, await Held(db, order.Id, child.Id, order.SellerId == child.SellerId), actorId, now, dispatch: false);
        await QueueFullSellerRefund(db, order, child, "cancel", now);
        MarkCancelled(db, child, now, "SellerCancelled");
        db.Notify(order, $"cancelled-{child.Id:N}", $"Seller order {child.Number} cancelled. {reason}".Trim(), now);
        db.Audit(actorId, "Hủy đơn", AuditTarget(order, child), now, reason, child.SellerId);
    }

    internal static async Task CloseTransport(IShopSession db, SellerOrder child, DateTimeOffset now, IShippingGateway? shipping = null)
    {
        if (shipping is not null)
            await shipping.CancelOrderParcelsAsync(db, child.Id, now);
    }

    private static Task Pack(IShopSession db, ShopActor actor, Order order, SellerOrder child, DateTimeOffset now)
    {
        RequireStatus(child, OrderStatus.Confirmed);
        child.ChangeStatus(OrderStatus.Packing, now);
        db.Audit(actor.UserId, "Đóng gói", AuditTarget(order, child), now, sellerId: child.SellerId);
        return Task.CompletedTask;
    }

    internal static async Task Dispatch(IShopSession db, ShopActor actor, Order order, SellerOrder child, string carrier, string tracking,
        DateTimeOffset now, bool hkTransport = false, IShippingGateway? shipping = null)
    {
        RequireStatus(child, OrderStatus.Packing);
        if (!hkTransport && shipping is not null && (await shipping.GetParcelSummariesAsync(db, [child.Id])).ContainsKey(child.Id))
            throw new ShopRuleException("Request pickup through HKShop transportation.");
        carrier = ShopRules.Text(carrier, 100, "Carrier");
        tracking = ShopRules.Text(tracking, 100, "Tracking number");
        await MoveHeld(db, await Held(db, order.Id, child.Id, order.SellerId == child.SellerId), actor.UserId, now, dispatch: true);
        db.Add(new Shipment { OrderId = order.Id, SellerOrderId = child.Id, Carrier = carrier, TrackingNumber = tracking, DispatchedAt = now });
        child.ChangeStatus(OrderStatus.Shipped, now);
        db.Notify(order, $"shipped-{child.Id:N}", $"Seller order {child.Number} shipped", now);
        db.Audit(actor.UserId, "Xuất hàng", AuditTarget(order, child), now, tracking, child.SellerId);
    }

    internal static async Task Deliver(IShopSession db, ShopActor actor, Order order, SellerOrder child, decimal? collected,
        string reference, DateTimeOffset now, bool hkTransport = false, IShippingGateway? shipping = null)
    {
        RequireStatus(child, OrderStatus.Shipped);
        if (!hkTransport && shipping is not null && (await shipping.GetParcelSummariesAsync(db, [child.Id])).ContainsKey(child.Id))
            throw new ShopRuleException("Only the assigned HKShop shipper can confirm delivery.");
        var payment = await FindPayment(db, order.Id);
        if (payment.Method == PaymentMethod.Cod)
        {
            if (collected != order.TotalVnd) throw new ShopRuleException("Collected amount must match the order total.");
            payment.CollectionReference = ShopRules.Text(reference, 100, "Collection reference");
            payment.PaidAt = now;
            payment.Status = PaymentStatus.Paid;
            order.PaymentStatus = PaymentStatus.Paid;
            var attempt = await db.First(db.Query<PaymentAttempt>().Where(a => a.OrderId == order.Id).OrderByDescending(a => a.AttemptNumber));
            if (attempt is not null && !attempt.AppliedToOrder)
            {
                attempt.Status = PaymentStatus.Paid;
                attempt.CapturedAmountVnd = order.TotalVnd;
                attempt.PaidAt = now;
                attempt.AppliedToOrder = true;
                await SettlementLedger.RecordCapture(db, attempt, now);
                await SettlementLedger.RecordSales(db, attempt, [child], now);
            }
        }

        child.ChangeStatus(OrderStatus.Delivered, now);
        child.DeliveredAt = now;
        db.Notify(order, $"delivered-{child.Id:N}", $"Seller order {child.Number} delivered", now);
        db.Audit(actor.UserId, "Xác nhận giao hàng", AuditTarget(order, child), now, sellerId: child.SellerId);
    }

    private static Task RecordDeliveryException(IShopSession db, ShopActor actor, Order order, SellerOrder child, string note,
        DateTimeOffset now)
    {
        RequireStatus(child, OrderStatus.Shipped);
        note = ShopRules.Text(note, 250, "Delivery note");
        order.ChangeStatus(order.Status, now, note);
        db.Audit(actor.UserId, "Sự cố giao hàng", AuditTarget(order, child), now, note, child.SellerId);
        return Task.CompletedTask;
    }

    private static async Task ReceiveUndelivered(IShopSession db, ShopActor actor, Order order, SellerOrder child, string reason,
        IReadOnlyList<ReturnQuantity> quantities, DateTimeOffset now, IShippingGateway? shipping = null)
    {
        RequireStatus(child, OrderStatus.Shipped);
        var parcel = shipping is null ? null : (await shipping.GetParcelSummariesAsync(db, [child.Id])).GetValueOrDefault(child.Id);
        if (parcel is not null && parcel.Stage != TransportStage.Returning)
            throw new ShopRuleException("The HKShop shipper must start the return before receipt can be confirmed.");
        reason = ShopRules.Text(reason, 250, "Undelivered reason");
        var items = await db.List(db.Query<OrderItem>().Where(i => i.SellerOrderId == child.Id).OrderBy(i => i.OfferId));
        var itemsById = items.ToDictionary(i => i.Id);
        if (quantities.Select(q => q.ItemId).Distinct().Count() != quantities.Count ||
            quantities.Any(q => !itemsById.TryGetValue(q.ItemId, out var item) || q.Quantity < 0 || q.Quantity > item.Quantity))
            throw new ShopRuleException("Invalid restock quantity.");
        var restock = quantities.Where(q => q.Quantity > 0).ToDictionary(q => q.ItemId, q => q.Quantity);
        foreach (var item in items.Where(i => restock.ContainsKey(i.Id)))
            await ReturnService.RestockOriginal(db, item, restock[item.Id], "Undelivered restock", actor.UserId, now);

        await QueueFullSellerRefund(db, order, child, "undeliverable", now);
        child.ChangeStatus(OrderStatus.Undeliverable, now);
        if (parcel is not null && shipping is not null)
            await shipping.CompleteReturnedParcelAsync(db, child.Id, now);
        db.Notify(order, $"undeliverable-{child.Id:N}", $"Seller order {child.Number} undeliverable", now);
        db.Audit(actor.UserId, "Nhận lại bưu kiện", AuditTarget(order, child), now, reason, child.SellerId);
    }

    private static OrderStatus FulfillmentStatus(IReadOnlyList<SellerOrder> children)
    {
        var remaining = children.Where(s => s.Status is not (OrderStatus.Cancelled or OrderStatus.Undeliverable)).ToList();
        return remaining.Count > 0
            ? remaining.Min(s => s.Status)
            : children.Any(s => s.Status == OrderStatus.Undeliverable) ? OrderStatus.Undeliverable : OrderStatus.Cancelled;
    }

    private static void RequireStatus(SellerOrder child, params OrderStatus[] allowed)
    {
        if (!allowed.Contains(child.Status))
            throw new ShopRuleException("The current seller order status does not allow this action.");
    }

    internal static Task<List<SellerOrder>> FindSellerOrders(IShopSession db, Guid orderId) =>
        db.List(db.Query<SellerOrder>().Where(s => s.OrderId == orderId).OrderBy(s => s.Id));

    private static string AuditTarget(Order order, SellerOrder child) => order.SellerId == child.SellerId ? order.Number : child.Number;

    private static void MarkCancelled(IShopSession db, SellerOrder child, DateTimeOffset now, string reason)
    {
        child.ChangeStatus(OrderStatus.Cancelled, now);
        db.IntegrationEvent("SellerOrder", child.Id.ToString("D"), "SellerOrderCancelled", new
        {
            SellerOrderId = child.Id, child.OrderId, child.SellerId, child.Number, Reason = reason, CancelledAt = now
        }, now, $"SellerOrder:{child.Id:N}:Cancelled");
    }

    private static async Task<bool> QueueFullSellerRefund(IShopSession db, Order order, SellerOrder child, string reason, DateTimeOffset now)
    {
        var items = await db.List(db.Query<OrderItem>().Where(i => i.SellerOrderId == child.Id).OrderBy(i => i.Id));
        var allocations = items.Select(i => new RefundAllocation(i.Id, i.Quantity, i.RefundFor(0, i.Quantity))).ToList();
        return await RefundWorkflow.QueueSellerRefund(db, order, child, allocations, child.ShippingVnd, $"{reason}:{child.Id:N}", now);
    }

    private static async Task CancelUnpaidPayment(IShopSession db, Order order)
    {
        var payment = await FindPayment(db, order.Id);
        if (payment.Status == PaymentStatus.Paid) return;
        payment.Status = PaymentStatus.Cancelled;
        payment.NextQueryAt = null;
        order.PaymentStatus = PaymentStatus.Cancelled;
        var attempts = await db.List(db.Query<PaymentAttempt>().Where(a => a.OrderId == order.Id &&
            (a.Status == PaymentStatus.Due || a.Status == PaymentStatus.Pending)));
        foreach (var attempt in attempts)
        {
            attempt.Status = PaymentStatus.Cancelled;
            attempt.NextQueryAt = null;
        }
    }

    internal static async Task<Order> FindOrder(IShopSession db, Guid id, string? owner = null) =>
        await db.First(db.Query<Order>().Where(o => o.Id == id && (owner == null || o.CustomerId == owner)))
            ?? throw new ShopRuleException("Order not found.");

    internal static async Task<Payment> FindPayment(IShopSession db, Guid id) =>
        await db.First(db.Query<Payment>().Where(p => p.OrderId == id))
            ?? throw new ShopRuleException("Payment record not found.");

    private static Task<List<StockReservation>> Held(IShopSession db, Guid orderId, Guid? sellerOrderId = null,
        bool includeLegacyHolds = false)
    {
        var holds = db.Query<StockReservation>().Where(r => r.OrderId == orderId && r.Status == ReservationStatus.Held);
        if (sellerOrderId is Guid childId)
        {
            var itemIds = db.Query<OrderItem>().Where(i => i.SellerOrderId == childId).Select(i => (long?)i.Id);
            holds = holds.Where(r => r.OrderItemId == null ? includeLegacyHolds : itemIds.Contains(r.OrderItemId));
        }
        return db.List(holds.OrderBy(r => r.InventoryBalanceId).ThenBy(r => r.Id));
    }

    private static async Task MoveHeld(IShopSession db, IReadOnlyList<StockReservation> reservations, string? actor,
        DateTimeOffset now, bool dispatch)
    {
        foreach (var held in reservations)
        {
            var balance = await db.First(db.Query<InventoryBalance>().Where(b => b.Id == held.InventoryBalanceId));
            if (balance is not null)
            {
                if (dispatch)
                    await InventoryService.DispatchOffer(db, balance, held.SkuId, held.OrderId, held.Quantity, "Order dispatched", actor, now);
                else
                    await InventoryService.ReleaseOffer(db, balance, held.SkuId, held.OrderId, held.Quantity, "Order released", actor, now);
            }
            else
            {
                var stock = await InventoryService.GetBalance(db, held.SkuId);
                if (dispatch) stock.Dispatch(held.Quantity);
                else stock.Release(held.Quantity);
                InventoryService.Movement(db, held.SkuId, held.OrderId, dispatch ? -held.Quantity : 0, -held.Quantity,
                    dispatch ? "Xuất hàng" : "Bỏ giữ hàng", actor, now);
            }
            held.Status = dispatch ? ReservationStatus.Consumed : ReservationStatus.Released;
        }
    }

    internal static async Task Release(IShopSession db, Order order, DateTimeOffset now, string? actor = null)
    {
        await MoveHeld(db, await Held(db, order.Id), actor, now, dispatch: false);
        await SetClaim(db, order.Id, ClaimStatus.Released);
    }

    internal static async Task SetClaim(IShopSession db, Guid order, ClaimStatus status)
    {
        var claim = await db.First(db.Query<PromotionClaim>().Where(c => c.OrderId == order));
        if (claim is not null) claim.Status = status;
    }
}
