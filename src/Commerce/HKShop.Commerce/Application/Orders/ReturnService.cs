using HKShop.Application.Catalog;
using HKShop.Application.Payments;
using HKShop.Application.Sellers;
using HKShop.Contracts.Ports;
using HKShop.Domain;
using HKShop.Domain.Orders;
using HKShop.Domain.Sellers;

namespace HKShop.Application.Orders;

public sealed class ReturnService(IShopStore store, TimeProvider time, IShippingGateway? shipping = null)
{
    public Task<List<ReturnRequest>> Queue(ShopActor actor)
    {
        actor.RequireStaff();
        return store.Read(db => WithItems(db, OpenRequests(db)));
    }

    public Task<List<ReturnRequest>> Queue(ShopActor actor, long sellerId) => store.Write(async db =>
    {
        await SellerAccess.Require(db, actor, sellerId, SellerPermission.Fulfillment);
        var sellerOrders = db.Query<SellerOrder>().Where(s => s.SellerId == sellerId).Select(s => s.Id);
        return await WithItems(db, OpenRequests(db, sellerOrders));
    });

    public Task<List<ReturnRequest>> SellerReturns(ShopActor actor, long sellerId, Guid sellerOrderId) => store.Write(async db =>
    {
        await SellerAccess.Require(db, actor, sellerId, SellerPermission.Fulfillment);
        if (await db.Count(db.Query<SellerOrder>().Where(s => s.Id == sellerOrderId && s.SellerId == sellerId)) == 0)
            throw new SellerAccessDeniedException();
        return await WithItems(db, db.Query<ReturnRequest>().Where(r => r.SellerOrderId == sellerOrderId)
            .OrderByDescending(r => r.RequestedAt).ThenByDescending(r => r.Id));
    });

    public Task<int> Request(ShopActor actor, Guid orderId, string reason, IReadOnlyList<ReturnQuantity> quantities) =>
        RequestCore(actor, orderId, null, reason, quantities);

    public Task<int> Request(ShopActor actor, Guid orderId, Guid sellerOrderId, string reason, IReadOnlyList<ReturnQuantity> quantities) =>
        RequestCore(actor, orderId, sellerOrderId, reason, quantities);

    private Task<int> RequestCore(ShopActor actor, Guid orderId, Guid? sellerOrderId, string reason, IReadOnlyList<ReturnQuantity> quantities)
    {
        actor.RequireCustomer();
        reason = ShopRules.Text(reason, 250, "Return reason");
        var selections = quantities.Where(q => q.Quantity != 0).ToList();
        if (selections.Count == 0 || selections.Any(q => q.Quantity < 0) ||
            selections.Select(q => q.ItemId).Distinct().Count() != selections.Count)
            throw new ShopRuleException("Select at least one item and a valid return quantity.");
        var selected = selections.ToDictionary(q => q.ItemId, q => q.Quantity);
        return store.Write(async db =>
        {
            await OrderService.FindOrder(db, orderId, actor.UserId);
            var selectedIds = selected.Keys.ToArray();
            var items = await db.List(db.Query<OrderItem>().Where(i => i.OrderId == orderId && selectedIds.Contains(i.Id)));
            if (items.Count != selected.Count) throw new ShopRuleException("The item does not belong to the order.");
            var childId = sellerOrderId ?? items[0].SellerOrderId;
            if (items.Any(i => i.SellerOrderId != childId))
                throw new ShopRuleException("Select items from the same seller order.");
            var child = await db.First(db.Query<SellerOrder>().Where(s => s.Id == childId && s.OrderId == orderId));
            if (child is null || child.Status != OrderStatus.Delivered)
                throw new ShopRuleException("The seller order has not been delivered.");
            var now = time.GetUtcNow();
            if (child.DeliveredAt is null || child.DeliveredAt.Value.AddDays(14) < now)
                throw new ShopRuleException("The 14-day return period has expired.");

            var row = new ReturnRequest { OrderId = orderId, SellerOrderId = child.Id, Reason = reason, RequestedAt = now };
            foreach (var item in items)
            {
                var committed = await db.Sum(from line in db.Query<ReturnItem>()
                    join request in db.Query<ReturnRequest>() on line.ReturnId equals request.Id
                    where line.OrderItemId == item.Id && request.Status != ReturnStatus.Rejected
                    select (decimal)(request.Status == ReturnStatus.Requested || request.Status == ReturnStatus.Approved
                        ? line.Requested : line.Accepted));
                var quantity = selected[item.Id];
                if (quantity > item.Quantity - committed)
                    throw new ShopRuleException("The requested quantity exceeds the quantity available for return.");
                row.Items.Add(new ReturnItem { SellerOrderId = child.Id, OrderItemId = item.Id, Requested = quantity });
            }
            db.Add(row);
            return 0;
        });
    }

    public Task<Guid> Review(ShopActor actor, Guid id, bool approved, string note) => ReviewCore(actor, null, id, approved, note);

    public Task<Guid> Review(ShopActor actor, long sellerId, Guid id, bool approved, string note) =>
        ReviewCore(actor, sellerId, id, approved, note);

    private Task<Guid> ReviewCore(ShopActor actor, long? sellerId, Guid id, bool approved, string note)
    {
        if (sellerId is null) actor.RequireStaff();
        note = ShopRules.Text(note, 250, "Note", !approved);
        return store.Write(async db =>
        {
            var (row, child) = await Find(db, actor, sellerId, id);
            if (row.Status != ReturnStatus.Requested) throw new ShopRuleException("The request has already been processed.");
            var order = await OrderService.FindOrder(db, row.OrderId);
            var now = time.GetUtcNow();
            row.Status = approved ? ReturnStatus.Approved : ReturnStatus.Rejected;
            row.StaffNote = note;
            db.Notify(order, $"return-review-{id:N}", approved ? "Return request approved" : "Return request rejected", now);
            db.Audit(actor.UserId, "Duyệt trả hàng", id.ToString(), now, note, child.SellerId);
            return sellerId is null ? order.Id : child.Id;
        });
    }

    public Task<Guid> Inspect(ShopActor actor, Guid id, IReadOnlyList<InspectedItem> inspection, string note) =>
        InspectCore(actor, null, id, inspection, note);

    public Task<Guid> Inspect(ShopActor actor, long sellerId, Guid id, IReadOnlyList<InspectedItem> inspection, string note) =>
        InspectCore(actor, sellerId, id, inspection, note);

    private Task<Guid> InspectCore(ShopActor actor, long? sellerId, Guid id, IReadOnlyList<InspectedItem> inspection, string note)
    {
        if (sellerId is null) actor.RequireStaff();
        note = ShopRules.Text(note, 250, "Inspection note");
        return store.Write(async db =>
        {
            var (row, child) = await Find(db, actor, sellerId, id);
            if (row.Status != ReturnStatus.Approved) throw new ShopRuleException("The request has not been approved or has already been inspected.");
            var order = await OrderService.FindOrder(db, row.OrderId);
            if (shipping is not null && await shipping.HasUnverifiedCashRemittanceAsync(db, child.Id))
                throw new ShopRuleException("Finance must reconcile the shipper's remittance before processing this refund.");
            var lines = await db.List(from line in db.Query<ReturnItem>()
                join item in db.Query<OrderItem>() on line.OrderItemId equals item.Id
                where line.ReturnId == id && item.SellerOrderId == child.Id
                orderby item.OfferId, item.Id
                select new { Line = line, Item = item });
            if (lines.Count == 0 || lines.Count != inspection.Count || inspection.Select(i => i.ItemId).Distinct().Count() != lines.Count)
                throw new ShopRuleException("Enter inspection results for each item.");
            var results = inspection.ToDictionary(i => i.ItemId);
            var now = time.GetUtcNow();
            var allocations = new List<RefundAllocation>();
            foreach (var entry in lines)
            {
                var (line, item) = (entry.Line, entry.Item);
                if (!results.TryGetValue(line.Id, out var result) || result.Accepted < 0 || result.Accepted > line.Requested ||
                    result.Restocked < 0 || result.Restocked > result.Accepted)
                    throw new ShopRuleException("The inspected quantity is invalid.");
                var prior = await db.Sum(db.Query<ReturnItem>().Where(i => i.OrderItemId == item.Id && i.ReturnId != id)
                    .Select(i => (decimal)i.Accepted));
                if (prior + result.Accepted > item.Quantity)
                    throw new ShopRuleException("The return quantity exceeds the quantity purchased.");
                if (result.Accepted > 0)
                    allocations.Add(new RefundAllocation(item.Id, result.Accepted, item.RefundFor((int)prior, result.Accepted)));
                line.Accepted = result.Accepted;
                line.Restocked = result.Restocked;
                if (line.Restocked > 0)
                    await RestockOriginal(db, item, line.Restocked, "Return restock", actor.UserId, now);
            }

            var refundQueued = allocations.Count > 0 &&
                await RefundWorkflow.QueueSellerRefund(db, order, child, allocations, 0, $"return:{id:N}", now, id);
            row.Status = refundQueued ? ReturnStatus.AwaitingRefund : ReturnStatus.Completed;
            row.StaffNote = note;
            db.Notify(order, $"return-inspection-{id:N}", refundQueued ? "Returned items inspected; refund pending" : "Returned items processed", now);
            db.Audit(actor.UserId, "Kiểm hàng trả", id.ToString(), now, note, child.SellerId);
            return sellerId is null ? order.Id : child.Id;
        });
    }

    internal static async Task RestockOriginal(IShopSession db, OrderItem item, int quantity, string reason, string? actorId, DateTimeOffset now)
    {
        var reservation = await db.First(db.Query<StockReservation>().Where(r => r.OrderId == item.OrderId
            && (r.OrderItemId == item.Id || (r.OrderItemId == null && r.SkuId == item.SkuId)))
            .OrderBy(r => r.InventoryBalanceId));
        var balances = db.Query<InventoryBalance>().Where(s => s.OfferId == item.OfferId && s.SellerId == item.SellerId);
        InventoryBalance? balance = null;
        if (reservation is not null)
        {
            if (reservation.Status != ReservationStatus.Consumed)
                throw new ShopRuleException("The item has not been dispatched.");
            balance = await db.First(balances.Where(s => s.Id == reservation.InventoryBalanceId));
        }
        else if (item.SellerId == Seller.FirstPartyId)
        {
            balance = await db.First(balances.Where(s => s.LocationId == SellerLocation.FirstPartyId));
        }
        if (balance is not null)
        {
            await InventoryService.RestockOffer(db, balance, item.SkuId, item.OrderId, quantity, reason, actorId, now);
            return;
        }
        if (item.OfferId > 0 || item.SellerId != Seller.FirstPartyId)
            throw new ShopRuleException("The location that dispatched this item was not found.");
        var legacy = await InventoryService.GetBalance(db, item.SkuId);
        legacy.Adjust(quantity);
        InventoryService.Movement(db, item.SkuId, item.OrderId, quantity, 0, reason, actorId, now);
    }

    private static IQueryable<ReturnRequest> OpenRequests(IShopSession db, IQueryable<Guid>? sellerOrders = null)
    {
        var query = db.Query<ReturnRequest>().Where(r => r.Status == ReturnStatus.Requested || r.Status == ReturnStatus.Approved);
        if (sellerOrders is not null) query = query.Where(r => sellerOrders.Contains(r.SellerOrderId));
        return query.OrderBy(r => r.RequestedAt).ThenBy(r => r.Id).Take(100);
    }

    private static async Task<List<ReturnRequest>> WithItems(IShopSession db, IQueryable<ReturnRequest> query)
    {
        var requests = await db.List(query);
        var ids = requests.Select(r => r.Id).ToArray();
        var items = (await db.List(db.Query<ReturnItem>().Where(i => ids.Contains(i.ReturnId)).OrderBy(i => i.Id)))
            .ToLookup(i => i.ReturnId);
        foreach (var request in requests) request.Items = items[request.Id].ToList();
        return requests;
    }

    private static async Task<(ReturnRequest Request, SellerOrder SellerOrder)> Find(IShopSession db, ShopActor actor, long? sellerId, Guid id)
    {
        if (sellerId is long scope) await SellerAccess.Require(db, actor, scope, SellerPermission.Fulfillment);
        var entry = await db.First(from row in db.Query<ReturnRequest>()
            join child in db.Query<SellerOrder>() on row.SellerOrderId equals child.Id
            where row.Id == id && row.OrderId == child.OrderId && (sellerId == null || child.SellerId == sellerId)
            select new { Request = row, SellerOrder = child });
        return entry is null ? throw new ShopRuleException("The return request was not found.") : (entry.Request, entry.SellerOrder);
    }
}
