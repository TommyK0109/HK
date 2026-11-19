using HKShop.Application;
using HKShop.Application.Catalog;
using HKShop.Application.Checkout;
using HKShop.Application.Orders;
using HKShop.Application.Payments;
using HKShop.Application.Reporting;
using HKShop.Application.Sellers;
using HKShop.Application.Settlement;
using HKShop.Application.Transportation;
using HKShop.Contracts.Ports;
using HKShop.Domain;
using HKShop.Domain.Orders;
using HKShop.Domain.Sellers;
using HKShop.Domain.Settlement;

namespace HKShop.Commerce.Application.Ports;

public sealed class CatalogOperationsAdapter(CatalogService catalog, InventoryService inventory) : ICatalogOperationsPort
{
    public async Task<IReadOnlyList<CatalogCategoryDto>> CategoriesAsync(bool activeOnly = true) =>
        (await catalog.Categories(activeOnly))
            .Select(c => new CatalogCategoryDto(c.Id, c.Name, c.Active))
            .ToList();

    public async Task<Page<CatalogProductDto>> ProductsAsync(ShopActor actor, int page)
    {
        var result = await catalog.Products(actor, page);
        return new Page<CatalogProductDto>(
            result.Items.Select(p => new CatalogProductDto(p.Id, p.CategoryId, p.Name, p.Slug, p.Description, p.LegacyPrice, p.Status)).ToList(),
            result.Number,
            result.Total,
            result.Size);
    }

    public async Task<CatalogEditDetailsDto> EditDetailsAsync(ShopActor actor, int id)
    {
        var details = await catalog.EditDetails(actor, id);
        var p = details.Product;
        return new CatalogEditDetailsDto(
            new CatalogProductDto(p.Id, p.CategoryId, p.Name, p.Slug, p.Description, p.LegacyPrice, p.Status),
            details.Variants,
            details.Images.Select(i => new CatalogImageDto(i.Id, i.ProductId, i.FileName, i.Alt)).ToList());
    }

    public Task<int> SaveCategoryAsync(ShopActor actor, long id, string name, bool active) =>
        catalog.SaveCategory(actor, id, name, active);

    public Task<int> SaveProductAsync(ShopActor actor, int id, string name, string slug, string description, long? categoryId) =>
        catalog.SaveProduct(actor, id, name, slug, description, categoryId);

    public Task<long> SaveSkuAsync(ShopActor actor, int productId, long skuId, string code, string size, string color, decimal price, bool active) =>
        catalog.SaveSku(actor, productId, skuId, code, size, color, price, active);

    public Task AddImageAsync(ShopActor actor, int productId, string alt, Stream file) =>
        catalog.AddImage(actor, productId, alt, file);

    public Task<int> ChangePublicationAsync(ShopActor actor, int id, bool publish) =>
        catalog.ChangePublication(actor, id, publish);

    public Task<Page<StockRow>> ListFirstPartyStockAsync(ShopActor actor, bool low, int page = 1) =>
        inventory.List(actor, low, page);

    public async Task<IReadOnlyList<StockMovementDto>> FirstPartyHistoryAsync(ShopActor actor, long skuId) =>
        (await inventory.History(actor, skuId))
            .Select(m => new StockMovementDto(m.Id, m.SkuId, m.OrderId, m.OnHandDelta, m.ReservedDelta, m.Reason, m.ActorId, m.OccurredAt))
            .ToList();

    public Task<int> AdjustFirstPartyStockAsync(ShopActor actor, long skuId, int delta, int threshold, string reason) =>
        inventory.Adjust(actor, skuId, delta, threshold, reason);
}

public sealed class CommerceOperationsAdapter(
    PromotionService promotions,
    OrderService orders,
    ReturnService returns,
    PaymentService payments,
    SettlementLedger settlement,
    SellerService sellers,
    SellerCatalogService sellerCatalog,
    PayoutService payouts,
    ReportingService reports) : ICommerceOperationsPort
{
    public async Task<IReadOnlyList<PromotionDto>> ListPromotionsAsync(ShopActor actor) =>
        (await promotions.List(actor))
            .Select(p => new PromotionDto(p.Id, p.Code, p.Active, p.IsPercentage, p.Value, p.MinimumVnd, p.UsageCap, p.StartsAt, p.EndsAt))
            .ToList();

    public Task<int> SavePromotionAsync(ShopActor actor, long id, string code, bool active, bool percentage,
        decimal value, decimal minimum, int cap, DateTimeOffset start, DateTimeOffset end) =>
        promotions.Save(actor, id, code, active, percentage, value, minimum, cap, start, end);

    public async Task<Page<EOfficeOrderSummaryDto>> ListOrdersAsync(ShopActor actor, OrderStatus? status, int page)
    {
        var result = await orders.List(actor, adminView: true, status, page);
        return new Page<EOfficeOrderSummaryDto>(
            result.Items.Select(o => new EOfficeOrderSummaryDto(o.Id, o.Number, o.CreatedAt, o.Status, o.TotalVnd)).ToList(),
            result.Number,
            result.Total,
            result.Size);
    }

    public async Task<EOfficeOrderDetailsDto?> OrderDetailsAsync(ShopActor actor, Guid id)
    {
        var details = await orders.Details(actor, id, adminView: true);
        if (details is null) return null;
        var o = details.Order;
        return new EOfficeOrderDetailsDto(
            o.Id,
            o.SellerId,
            o.Number,
            o.CreatedAt,
            o.Status,
            o.PaymentStatus,
            o.Recipient,
            o.Phone,
            o.ShippingAddress,
            o.ShippingVnd,
            o.TotalVnd,
            new EOfficePaymentDto(o.Payment.OrderId, o.Payment.Method, o.Payment.Status, o.Payment.AmountVnd, o.Payment.Issue),
            o.Shipment is null ? null : new EOfficeShipmentDto(o.Shipment.SellerOrderId, o.Shipment.OrderId, o.Shipment.Carrier, o.Shipment.TrackingNumber, o.Shipment.DispatchedAt),
            o.SellerOrders.Select(so => new EOfficeSellerOrderDto(
                so.Id, so.OrderId, so.SellerId, so.Number, so.Status, so.SubtotalVnd, so.SellerDiscountVnd, so.PlatformDiscountVnd,
                so.ShippingVnd, so.TotalVnd, so.CommissionVnd, so.CreatedAt, so.DeliveredAt,
                so.Shipment is null ? null : new EOfficeShipmentDto(so.Shipment.SellerOrderId, so.Shipment.OrderId, so.Shipment.Carrier, so.Shipment.TrackingNumber, so.Shipment.DispatchedAt),
                so.Transport)).ToList(),
            o.Items.Select(i => new EOfficeOrderItemDto(i.Id, i.OrderId, i.SellerOrderId, i.SellerId, i.SkuId, i.OfferId, i.SkuCode, i.SellerSkuCode, i.Name, i.Quantity, i.UnitPriceVnd, i.DiscountVnd)).ToList(),
            o.Events.Select(e => new EOfficeOrderEventDto(e.Id, e.Status, e.OccurredAt, e.Note)).ToList(),
            o.PaymentAttempts.Select(a => new EOfficePaymentAttemptDto(a.Id, a.OrderId, a.SellerOrderId, a.AttemptNumber, a.Method, a.Status, a.AmountVnd, a.CapturedAmountVnd, a.ProviderInvoice, a.ProviderTransaction, a.AppliedToOrder, a.Issue)).ToList(),
            details.Returns.Select(MapReturn).ToList(),
            details.Refunds.Select(MapRefund).ToList(),
            details.SellerNames);
    }

    public Task<int> CancelOrderAsync(ShopActor actor, Guid id, string reason) => orders.Cancel(actor, id, reason, adminView: true);
    public Task<Guid> CancelSellerOrderAsync(ShopActor actor, Guid sellerOrderId, string reason) => orders.CancelSellerOrder(actor, sellerOrderId, reason);
    public Task<int> StartPackingOrderAsync(ShopActor actor, Guid id) => orders.StartPacking(actor, id);
    public Task<Guid> StartPackingSellerOrderAsync(ShopActor actor, Guid sellerOrderId) => orders.StartPackingSellerOrder(actor, sellerOrderId);
    public Task<int> DispatchOrderAsync(ShopActor actor, Guid id, string carrier, string tracking) => orders.Dispatch(actor, id, carrier, tracking);
    public Task<Guid> DispatchSellerOrderAsync(ShopActor actor, Guid sellerOrderId, string carrier, string tracking) => orders.DispatchSellerOrder(actor, sellerOrderId, carrier, tracking);
    public Task<int> DeliverOrderAsync(ShopActor actor, Guid id, decimal? collected, string reference) => orders.Deliver(actor, id, collected, reference);
    public Task<Guid> DeliverSellerOrderAsync(ShopActor actor, Guid sellerOrderId, decimal? collected, string reference) => orders.DeliverSellerOrder(actor, sellerOrderId, collected, reference);
    public Task<int> DeliveryExceptionOrderAsync(ShopActor actor, Guid id, string note) => orders.DeliveryException(actor, id, note);
    public Task<Guid> DeliveryExceptionSellerOrderAsync(ShopActor actor, Guid sellerOrderId, string note) => orders.DeliveryExceptionSellerOrder(actor, sellerOrderId, note);
    public Task<int> ReceiveUndeliveredOrderAsync(ShopActor actor, Guid id, string reason, IReadOnlyList<ReturnQuantity> quantities) => orders.ReceiveUndelivered(actor, id, reason, quantities);
    public Task<Guid> ReceiveUndeliveredSellerOrderAsync(ShopActor actor, Guid sellerOrderId, string reason, IReadOnlyList<ReturnQuantity> quantities) => orders.ReceiveUndeliveredSellerOrder(actor, sellerOrderId, reason, quantities);

    public async Task<IReadOnlyList<EOfficeReturnDto>> ReturnQueueAsync(ShopActor actor) =>
        (await returns.Queue(actor)).Select(MapReturn).ToList();

    public Task<Guid> ReviewReturnAsync(ShopActor actor, Guid id, bool approved, string note) => returns.Review(actor, id, approved, note);
    public Task<Guid> InspectReturnAsync(ShopActor actor, Guid id, IReadOnlyList<InspectedItem> inspection, string note) => returns.Inspect(actor, id, inspection, note);

    public async Task<IReadOnlyList<PaymentIssue>> PaymentIssuesAsync(ShopActor actor) => await payments.Issues(actor);
    public async Task<IReadOnlyList<EOfficeRefundDto>> PendingRefundsAsync(ShopActor actor) => (await payments.PendingRefunds(actor)).Select(MapRefund).ToList();
    public Task<IReadOnlyList<SettlementPaymentRow>> RecentLedgerPaymentsAsync(ShopActor actor) => settlement.RecentPayments(actor);
    public Task<IReadOnlyList<SettlementRefundRow>> RecentLedgerRefundsAsync(ShopActor actor) => settlement.RecentRefunds(actor);
    public Task ReconcilePaymentAsync(ShopActor actor, Guid orderId, CancellationToken cancellationToken) => payments.Reconcile(actor, orderId, cancellationToken);
    public Task<int> CompleteRefundAsync(ShopActor actor, long refundId, string reference) => payments.CompleteRefund(actor, refundId, reference);

    public Task<Page<PlatformSeller>> PlatformSellersAsync(ShopActor actor, int page) => sellers.PlatformList(actor, page);
    public Task<bool> SetSellerStatusAsync(ShopActor actor, long sellerId, SellerStatus status, string? reason) => sellers.SetStatus(actor, sellerId, status, reason);
    public Task<MarketplaceModeration> ModerationAsync(ShopActor actor, int offerPage, int proposalPage) => sellerCatalog.Moderation(actor, offerPage, proposalPage);
    public Task<bool> ReviewOfferAsync(ShopActor actor, long offerId, bool approve) => sellerCatalog.ReviewOffer(actor, offerId, approve);
    public Task<int?> ReviewProposalAsync(ShopActor actor, long proposalId, bool approve) => sellerCatalog.ReviewProposal(actor, proposalId, approve);

    public async Task<Page<PayoutQueueItemDto>> PayoutQueueAsync(ShopActor actor, int page)
    {
        var result = await payouts.Queue(actor, page);
        return new Page<PayoutQueueItemDto>(
            result.Items.Select(r => new PayoutQueueItemDto(
                r.Payout.Id, r.Payout.SellerId, r.SellerName, r.Payout.AmountVnd, r.Payout.Status,
                r.Payout.ProviderReference, r.Payout.Issue, r.Payout.ReviewNote,
                r.Payout.RequestedAt, r.Payout.NextAttemptAt, r.Payout.CompletedAt)).ToList(),
            result.Number, result.Total, result.Size);
    }

    public Task<int> ReviewPayoutAsync(ShopActor actor, Guid id, bool approve, string note) => payouts.Review(actor, id, approve, note);
    public Task ReconcilePayoutAsync(ShopActor actor, Guid id, CancellationToken cancellationToken) => payouts.Reconcile(actor, id, cancellationToken);

    public Task<SalesSummary> SalesReportAsync(ShopActor actor, DateOnly start, DateOnly end) => reports.Sales(actor, start, end);

    public async Task<Page<AuditEntryDto>> AuditReportAsync(ShopActor actor, DateOnly start, DateOnly end, string? action, int page)
    {
        var result = await reports.Audit(actor, start, end, action, page);
        return new Page<AuditEntryDto>(
            result.Items.Select(a => new AuditEntryDto(a.Id, a.SellerId, a.ActorId, a.Action, a.Target, a.Reason, a.OccurredAt)).ToList(),
            result.Number, result.Total, result.Size);
    }

    public async Task<IReadOnlyList<FailedMailDto>> FailedMailAsync(ShopActor actor) =>
        (await reports.FailedMail(actor)).Select(m => new FailedMailDto(m.Id, m.Subject, m.Attempts)).ToList();

    private static EOfficeReturnDto MapReturn(ReturnRequest row) =>
        new(row.Id, row.OrderId, row.SellerOrderId, row.Status, row.Reason, row.StaffNote, row.RequestedAt,
            row.Items.Select(i => new EOfficeReturnItemDto(i.Id, i.ReturnId, i.SellerOrderId, i.OrderItemId, i.Requested, i.Accepted, i.Restocked)).ToList());

    private static EOfficeRefundDto MapRefund(Refund r) =>
        new(r.Id, r.OrderId, r.PaymentAttemptId, r.SellerOrderId, r.ReturnId, r.BusinessKey, r.AmountVnd, r.ShippingVnd,
            r.Status, r.Simulated, r.Reference, r.ProviderReference, r.Issue, r.NextAttemptAt, r.RequestedAt, r.CompletedAt);
}

public sealed class ShippingCommerceCallbackAdapter : IShippingCommerceCallbackPort
{
    public async Task<SellerLocationSummary?> GetSellerLocationAsync(IShopSession db, ShopActor actor, long sellerId)
    {
        if (!actor.Admin) await SellerAccess.Require(db, actor, sellerId, SellerPermission.Fulfillment);
        var loc = await db.First(db.Query<SellerLocation>().Where(l => l.SellerId == sellerId && l.Active));
        return loc is null ? null : new SellerLocationSummary(loc.Id, loc.SellerId, loc.Name, loc.PickupAddress, loc.Latitude, loc.Longitude, loc.Active);
    }

    public async Task SetSellerLocationAsync(IShopSession db, ShopActor actor, long sellerId, string address, double latitude, double longitude, DateTimeOffset now)
    {
        if (!actor.Admin) await SellerAccess.Require(db, actor, sellerId, SellerPermission.Fulfillment);
        var location = await db.First(db.Query<SellerLocation>().Where(l => l.SellerId == sellerId && l.Active))
            ?? throw new ShopRuleException("The seller does not have an active storage location.");
        var pin = TransportPoint.Require(latitude, longitude, "pickup");
        location.PickupAddress = ShopRules.Text(address, 300, "Pickup address");
        location.Latitude = pin.Latitude;
        location.Longitude = pin.Longitude;
        db.Audit(actor.UserId, "Transport pickup location updated", location.Id.ToString(), now, sellerId: sellerId);
    }

    public async Task<TransportOrderSnapshot> RequireSellerOrderForTransportAsync(IShopSession db, ShopActor actor, long sellerId, Guid sellerOrderId)
    {
        if (!actor.Staff && !actor.Admin) await SellerAccess.Require(db, actor, sellerId, SellerPermission.Fulfillment);
        var snap = await GetOrderSnapshotAsync(db, sellerOrderId);
        if (snap.SellerId != sellerId) throw new ShopRuleException("Seller order not found.");
        return snap;
    }

    public async Task<TransportOrderSnapshot> GetOrderSnapshotAsync(IShopSession db, Guid sellerOrderId)
    {
        var child = await db.First(db.Query<SellerOrder>().Where(s => s.Id == sellerOrderId))
            ?? throw new ShopRuleException("Seller order not found.");
        var order = await OrderService.FindOrder(db, child.OrderId);
        var payment = await OrderService.FindPayment(db, order.Id);
        return new TransportOrderSnapshot(
            child.Id, order.Id, child.SellerId, child.Number, child.Status, child.TotalVnd, child.ShippingVnd,
            payment.Method, payment.Status, order.Recipient, order.Phone, order.ShippingAddress);
    }

    public Task AuditSellerTransportAsync(IShopSession db, string? actorId, string action, string target, DateTimeOffset now, string reason = "", long? sellerId = null)
    {
        db.Audit(actorId, action, target, now, reason, sellerId);
        return Task.CompletedTask;
    }

    public async Task NotifyReturnToSenderAsync(IShopSession db, Guid sellerOrderId, DateTimeOffset now)
    {
        var child = await db.First(db.Query<SellerOrder>().Where(s => s.Id == sellerOrderId))
            ?? throw new ShopRuleException("Seller order not found.");
        var order = await OrderService.FindOrder(db, child.OrderId);
        db.Notify(order, $"return-to-sender-{sellerOrderId:N}", $"Seller order {child.Number} is returning to the seller", now);
    }

    public async Task OnParcelPickedUpAsync(IShopSession db, ShopActor actor, Guid sellerOrderId, DateTimeOffset now)
    {
        var child = await db.First(db.Query<SellerOrder>().Where(s => s.Id == sellerOrderId))
            ?? throw new ShopRuleException("Seller order not found.");
        var order = await OrderService.FindOrder(db, child.OrderId);
        await OrderService.Dispatch(db, actor, order, child, "HKShop", child.Number, now, hkTransport: true);
        await OrderService.UpdateSummary(db, order, await OrderService.FindSellerOrders(db, order.Id), now);
    }

    public async Task OnParcelDeliveredAsync(IShopSession db, ShopActor actor, Guid sellerOrderId, DoorPayment? doorPayment, bool fundsVerified, decimal shipperFeeVnd, DateTimeOffset now)
    {
        var child = await db.First(db.Query<SellerOrder>().Where(s => s.Id == sellerOrderId))
            ?? throw new ShopRuleException("Seller order not found.");
        var order = await OrderService.FindOrder(db, child.OrderId);
        await OrderService.Deliver(db, actor, order, child, null, "", now, hkTransport: true);
        if (doorPayment == DoorPayment.Cash)
        {
            if (fundsVerified)
                await PostRetainedFee(db, child, now);
        }
        else
        {
            await SettlementLedger.Post(db, $"transport-earned:{sellerOrderId:N}", LedgerEventType.ShipperFeeEarned, now,
                [new(LedgerAccountType.TransportExpense, null, -shipperFeeVnd),
                 new(LedgerAccountType.ShipperPayable, null, shipperFeeVnd)], sellerOrderId: sellerOrderId);
        }
        await OrderService.UpdateSummary(db, order, await OrderService.FindSellerOrders(db, order.Id), now);
    }

    public async Task OnDoorPaymentVerifiedAsync(IShopSession db, Guid sellerOrderId, string? transactionReference, bool postRetainedFee, DateTimeOffset now)
    {
        var child = await db.First(db.Query<SellerOrder>().Where(s => s.Id == sellerOrderId))
            ?? throw new ShopRuleException("Seller order not found.");
        var order = await OrderService.FindOrder(db, child.OrderId);
        var payment = await OrderService.FindPayment(db, order.Id);
        var attempts = await db.List(db.Query<PaymentAttempt>().Where(a => a.OrderId == order.Id));
        var capture = new PaymentAttempt
        {
            OrderId = order.Id, SellerOrderId = child.Id, AttemptNumber = attempts.Count + 1,
            Method = PaymentMethod.TransportAtDelivery, Status = PaymentStatus.Paid, AmountVnd = child.TotalVnd,
            CapturedAmountVnd = child.TotalVnd, ProviderTransaction = transactionReference, AppliedToOrder = true, PaidAt = now, CreatedAt = now
        };
        db.Add(capture);
        await SettlementLedger.RecordCapture(db, capture, now);
        await SettlementLedger.RecordSales(db, capture, [child], now);
        await OrderService.UpdatePaymentStatus(db, order, payment, await OrderService.FindSellerOrders(db, order.Id), now, child.Id);
        if (postRetainedFee)
            await PostRetainedFee(db, child, now);
    }

    public async Task OnPickupDeadlineExpiredAsync(IShopSession db, Guid sellerOrderId, DateTimeOffset now)
    {
        var child = await db.First(db.Query<SellerOrder>().Where(s => s.Id == sellerOrderId))
            ?? throw new ShopRuleException("Seller order not found.");
        var order = await OrderService.FindOrder(db, child.OrderId);
        await OrderService.CancelChild(db, order, child, now, null, "The seller missed the 48-hour pickup request deadline.");
        await OrderService.UpdateSummary(db, order, await OrderService.FindSellerOrders(db, order.Id), now);
    }

    public async Task PostShipperPayoutLedgerAsync(IShopSession db, string? actorId, Guid sellerOrderId, decimal shipperFeeVnd, string reference, DateTimeOffset now)
    {
        await SettlementLedger.Post(db, $"transport-paid:{sellerOrderId:N}", LedgerEventType.ShipperFeePaid, now,
            [new(LedgerAccountType.ShipperPayable, null, -shipperFeeVnd),
             new(LedgerAccountType.PaymentClearing, null, shipperFeeVnd)], sellerOrderId: sellerOrderId);
        db.Audit(actorId, "Transport shipper payout reconciled", sellerOrderId.ToString(), now, reference);
    }

    public async Task RequireShipperEligibilityAsync(IShopSession db, string userId)
    {
        if (await db.Count(db.Query<SellerMembership>().Where(m => m.UserId == userId && m.Status == SellerMembershipStatus.Active)) > 0 ||
            await db.Count(db.Query<Order>().Where(o => o.CustomerId == userId)) > 0)
            throw new ShopRuleException("An account with an active seller membership or customer orders cannot become a shipper.");
    }

    private static Task PostRetainedFee(IShopSession db, SellerOrder child, DateTimeOffset now) =>
        SettlementLedger.Post(db, $"transport-retained:{child.Id:N}", LedgerEventType.ShipperFeePaid, now,
            [new(LedgerAccountType.TransportExpense, null, -child.ShippingVnd),
             new(LedgerAccountType.PaymentClearing, null, child.ShippingVnd)], sellerOrderId: child.Id);
}
