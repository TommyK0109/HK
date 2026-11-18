using System.Net.Http;
using HKShop.Application;
using HKShop.Application.Catalog;
using HKShop.Application.Checkout;
using HKShop.Application.Orders;
using HKShop.Application.Payments;
using HKShop.Application.Reporting;
using HKShop.Application.Sellers;
using HKShop.Domain;
using HKShop.Domain.Catalog;
using HKShop.Domain.Orders;
using HKShop.Domain.Promotions;
using HKShop.Domain.Sellers;
using HKShop.Infrastructure.Payments;
using HKShop.Infrastructure.Persistence;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Xunit;

namespace HKShop.IntegrationTests;

public sealed partial class SqlMarketplaceFulfillmentTests
{
    [SqlFact]
    public async Task IndependentSellerFulfillment_OneSellerShipsWhileOtherCancelsAndReleasesOnlyItsStock()
    {
        var factory = SqlTestDatabase.Create();
        await using var context = factory.CreateDbContext();
        try
        {
            await context.Database.MigrateAsync();
            var fixture = await SeedMarketplaceAsync(factory, context);

            // Seller 1 sells Offer 1 (SKU1: 200k x 1); Seller 2 sells Offer 2A (SKU1: 180k x 1) + Offer 2B (SKU2: 100k x 2)
            await fixture.Cart.SetOffer(fixture.Buyer.UserId, null, fixture.Offer1Id, 1, add: false);
            await fixture.Cart.SetOffer(fixture.Buyer.UserId, null, fixture.Offer2AId, 1, add: false);
            await fixture.Cart.SetOffer(fixture.Buyer.UserId, null, fixture.Offer2BId, 2, add: false);

            var preview = await fixture.Checkout.Quote(fixture.Buyer, fixture.AddressId, PaymentMethod.SePay, "SAVE50K");
            var orderId = await fixture.Checkout.Place(fixture.Buyer, preview.Quote);
            var orderNumber = await context.Set<Order>().Where(o => o.Id == orderId).Select(o => o.Number).SingleAsync();
            Assert.True(await fixture.Payments.Apply(new GatewayOutcome(orderNumber, "SEPAY-FULFILL-1", preview.Total, "VND", true)));

            var sellerOrders = await context.Set<SellerOrder>().AsNoTracking()
                .Where(s => s.OrderId == orderId)
                .OrderBy(s => s.SellerId)
                .ToListAsync();
            var so1 = sellerOrders.Single(s => s.SellerId == Seller.FirstPartyId);
            var so2 = sellerOrders.Single(s => s.SellerId == fixture.Seller2Id);

            // Cross-seller authorization check: Seller 1 owner cannot touch Seller 2's SellerOrder and vice versa.
            await Assert.ThrowsAsync<ShopRuleException>(() =>
                fixture.Orders.StartPackingSellerOrder(fixture.Seller1Owner, so2.Id, Seller.FirstPartyId));
            await Assert.ThrowsAsync<ShopRuleException>(() =>
                fixture.Orders.CancelSellerOrder(fixture.Seller2Owner, so1.Id, "Unauthorized", fixture.Seller2Id));
            await Assert.ThrowsAsync<SellerAccessDeniedException>(() =>
                fixture.Orders.StartPackingSellerOrder(fixture.Seller2Owner, so1.Id, Seller.FirstPartyId));

            // Seller 1 packs and dispatches its part of the order.
            await fixture.Orders.StartPackingSellerOrder(fixture.Seller1Owner, so1.Id, Seller.FirstPartyId);
            await fixture.Orders.DispatchSellerOrder(fixture.Seller1Owner, so1.Id, "GHN", "GHN-S1-001", Seller.FirstPartyId);

            // Seller 2 cancels its SellerOrder while Seller 1 is already shipped.
            await fixture.Orders.CancelSellerOrder(fixture.Seller2Owner, so2.Id, "Out of stock at warehouse", fixture.Seller2Id);

            // Whole-order buyer cancellation is now blocked because Seller 1 has already shipped.
            await Assert.ThrowsAsync<ShopRuleException>(() =>
                fixture.Orders.Cancel(fixture.Buyer, orderId, "Too late"));

            await using var mid = factory.CreateDbContext();
            var midDetails = Assert.IsType<OrderDetails>(await fixture.Orders.Details(fixture.Buyer, orderId));
            Assert.Equal(OrderStatus.Shipped, midDetails.Order.Status);
            Assert.Equal(2, midDetails.Order.SellerOrders.Count);

            var midSo1 = midDetails.Order.SellerOrders.Single(s => s.Id == so1.Id);
            var midSo2 = midDetails.Order.SellerOrders.Single(s => s.Id == so2.Id);
            Assert.Equal(OrderStatus.Shipped, midSo1.Status);
            Assert.NotNull(midSo1.Shipment);
            Assert.Equal(("GHN", "GHN-S1-001"), (midSo1.Shipment!.Carrier, midSo1.Shipment.TrackingNumber));
            Assert.Equal(OrderStatus.Cancelled, midSo2.Status);
            Assert.NotNull(midSo2.CancelledAt);

            // Only Seller 2's holds were released; Seller 1's hold was consumed on dispatch (OnHand 10 -> 9).
            var inv1 = await mid.Set<InventoryBalance>().SingleAsync(b => b.OfferId == fixture.Offer1Id);
            var sb1 = await mid.Set<StockBalance>().SingleAsync(s => s.SkuId == fixture.Sku1Id);
            var inv2A = await mid.Set<InventoryBalance>().SingleAsync(b => b.OfferId == fixture.Offer2AId);
            var inv2B = await mid.Set<InventoryBalance>().SingleAsync(b => b.OfferId == fixture.Offer2BId);
            Assert.Equal((9, 0), (inv1.OnHand, inv1.Reserved));
            Assert.Equal((9, 0), (sb1.OnHand, sb1.Reserved));
            Assert.Equal((10, 0), (inv2A.OnHand, inv2A.Reserved));
            Assert.Equal((10, 0), (inv2B.OnHand, inv2B.Reserved));

            var reservations = await mid.Set<StockReservation>().Where(r => r.OrderId == orderId).ToListAsync();
            Assert.Equal(ReservationStatus.Consumed, reservations.Single(r => r.OfferId == fixture.Offer1Id).Status);
            Assert.Equal(ReservationStatus.Released, reservations.Single(r => r.OfferId == fixture.Offer2AId).Status);
            Assert.Equal(ReservationStatus.Released, reservations.Single(r => r.OfferId == fixture.Offer2BId).Status);

            // Parent coupon stays redeemed because Seller 1 fulfilled its part of the order.
            Assert.Equal(ClaimStatus.Redeemed, (await mid.Set<PromotionClaim>().SingleAsync(c => c.OrderId == orderId)).Status);

            // Refund is queued solely for Seller 2's cancelled total (including Seller 2 shipping) with matching RefundItems.
            var cancelRefund = Assert.Single(midDetails.Refunds);
            Assert.Equal(so2.Id, cancelRefund.SellerOrderId);
            Assert.Equal(so2.TotalVnd, cancelRefund.AmountVnd);
            Assert.Equal(so2.ShippingVnd, cancelRefund.ShippingVnd);
            var refundItems = await mid.Set<RefundItem>().Where(ri => ri.RefundId == cancelRefund.Id).ToListAsync();
            Assert.Equal(2, refundItems.Count);
            Assert.All(refundItems, ri => Assert.Equal(so2.Id, ri.SellerOrderId));
            Assert.Equal(so2.TotalVnd - so2.ShippingVnd, refundItems.Sum(ri => ri.AmountVnd));

            // SellerOrderCancelled outbox event emitted for Seller 2 only.
            var cancelEvents = await mid.Set<IntegrationOutboxEvent>().Where(e => e.EventType == "SellerOrderCancelled").ToListAsync();
            var cancelEvent = Assert.Single(cancelEvents);
            Assert.Equal(so2.Id.ToString("D"), cancelEvent.AggregateId);

            // Privacy/scope check: cancelled Seller 2 order no longer exposes buyer delivery details.
            var s2Details = Assert.IsType<SellerOrderDetails>(await fixture.Orders.SellerDetails(fixture.Seller2Owner, fixture.Seller2Id, so2.Id));
            Assert.Equal("", s2Details.ShippingAddress);

            // Seller 1 delivers -> parent Order transitions to Delivered while Seller 2 remains Cancelled.
            await fixture.Orders.DeliverSellerOrder(fixture.Seller1Owner, so1.Id, null, "", Seller.FirstPartyId);
            var finalDetails = Assert.IsType<OrderDetails>(await fixture.Orders.Details(fixture.Buyer, orderId));
            Assert.Equal(OrderStatus.Delivered, finalDetails.Order.Status);
            Assert.NotNull(finalDetails.Order.DeliveredAt);
            Assert.Equal(OrderStatus.Delivered, finalDetails.Order.SellerOrders.Single(s => s.Id == so1.Id).Status);
            Assert.Equal(OrderStatus.Cancelled, finalDetails.Order.SellerOrders.Single(s => s.Id == so2.Id).Status);
        }
        finally
        {
            await context.Database.EnsureDeletedAsync();
        }
    }

    [SqlFact]
    public async Task PartialAndMultiStepReturns_AffectOnlyTargetedSellerAndLinesAndRestockOriginalLocations()
    {
        var factory = SqlTestDatabase.Create();
        await using var context = factory.CreateDbContext();
        try
        {
            await context.Database.MigrateAsync();
            var fixture = await SeedMarketplaceAsync(factory, context);

            // Seller 1: Offer 1 x 3 (600k); Seller 2: Offer 2A x 2 (360k) + Offer 2B x 1 (100k); SAVE50K creates odd line discounts
            await fixture.Cart.SetOffer(fixture.Buyer.UserId, null, fixture.Offer1Id, 3, add: false);
            await fixture.Cart.SetOffer(fixture.Buyer.UserId, null, fixture.Offer2AId, 2, add: false);
            await fixture.Cart.SetOffer(fixture.Buyer.UserId, null, fixture.Offer2BId, 1, add: false);

            var preview = await fixture.Checkout.Quote(fixture.Buyer, fixture.AddressId, PaymentMethod.SePay, "SAVE50K");
            var orderId = await fixture.Checkout.Place(fixture.Buyer, preview.Quote);
            var orderNumber = await context.Set<Order>().Where(o => o.Id == orderId).Select(o => o.Number).SingleAsync();
            Assert.True(await fixture.Payments.Apply(new GatewayOutcome(orderNumber, "SEPAY-RET-1", preview.Total, "VND", true)));

            var sellerOrders = await context.Set<SellerOrder>().AsNoTracking().Where(s => s.OrderId == orderId).ToListAsync();
            var so1 = sellerOrders.Single(s => s.SellerId == Seller.FirstPartyId);
            var so2 = sellerOrders.Single(s => s.SellerId == fixture.Seller2Id);
            var items = await context.Set<OrderItem>().AsNoTracking().Where(i => i.OrderId == orderId).ToListAsync();
            var item1 = items.Single(i => i.OfferId == fixture.Offer1Id);
            var item2A = items.Single(i => i.OfferId == fixture.Offer2AId);
            var item2B = items.Single(i => i.OfferId == fixture.Offer2BId);

            // Cannot request return before delivery.
            await Assert.ThrowsAsync<ShopRuleException>(() =>
                fixture.Returns.Request(fixture.Buyer, orderId, so2.Id, "Early return", [new ReturnQuantity(item2A.Id, 1)]));

            // Both sellers pack, ship, and deliver.
            await fixture.Orders.StartPackingSellerOrder(fixture.Seller1Owner, so1.Id, Seller.FirstPartyId);
            await fixture.Orders.DispatchSellerOrder(fixture.Seller1Owner, so1.Id, "GHN", "TRK-S1", Seller.FirstPartyId);
            await fixture.Orders.DeliverSellerOrder(fixture.Seller1Owner, so1.Id, null, "", Seller.FirstPartyId);

            await fixture.Orders.StartPackingSellerOrder(fixture.Seller2Owner, so2.Id, fixture.Seller2Id);
            await fixture.Orders.DispatchSellerOrder(fixture.Seller2Owner, so2.Id, "GHTK", "TRK-S2", fixture.Seller2Id);
            await fixture.Orders.DeliverSellerOrder(fixture.Seller2Owner, so2.Id, null, "", fixture.Seller2Id);

            // Cross-seller items cannot be mixed in one return request.
            await Assert.ThrowsAsync<ShopRuleException>(() =>
                fixture.Returns.Request(fixture.Buyer, orderId, "Mixed", [
                    new ReturnQuantity(item1.Id, 1),
                    new ReturnQuantity(item2A.Id, 1)
                ]));

            // Rejected return leaves item quantity eligible for a fresh return request.
            await fixture.Returns.Request(fixture.Buyer, orderId, so2.Id, "Initial request", [new ReturnQuantity(item2A.Id, 1)]);
            var rejected = Assert.Single(await fixture.Returns.Queue(fixture.Seller2Owner, fixture.Seller2Id));
            await fixture.Returns.Review(fixture.Seller2Owner, fixture.Seller2Id, rejected.Id, false, "Please include unboxing photo");
            Assert.Empty(await fixture.Returns.Queue(fixture.Seller2Owner, fixture.Seller2Id));

            // Buyer submits partial return #1: 1 of 2 units on Seller 2's Offer 2A (leaving Offer 2B and Seller 1 untouched).
            await fixture.Returns.Request(fixture.Buyer, orderId, so2.Id, "Wrong fit", [new ReturnQuantity(item2A.Id, 1)]);

            // Routed strictly to Seller 2's queue, not Seller 1's.
            Assert.Empty(await fixture.Returns.Queue(fixture.Seller1Owner, Seller.FirstPartyId));
            var s2Queue = await fixture.Returns.Queue(fixture.Seller2Owner, fixture.Seller2Id);
            var return1 = Assert.Single(s2Queue);
            var return1Line = Assert.Single(return1.Items);

            // Seller 1 cannot review or inspect Seller 2's return.
            await Assert.ThrowsAsync<ShopRuleException>(() =>
                fixture.Returns.Review(fixture.Seller1Owner, Seller.FirstPartyId, return1.Id, true, "Hijack"));

            await fixture.Returns.Review(fixture.Seller2Owner, fixture.Seller2Id, return1.Id, true, "Approved return 1");
            await fixture.Returns.Inspect(fixture.Seller2Owner, fixture.Seller2Id, return1.Id,
                [new InspectedItem(return1Line.Id, Accepted: 1, Restocked: 1)], "Seal intact; restocked 1 unit");

            await using (var afterR1 = factory.CreateDbContext())
            {
                // Offer 2A dispatched 2 (10 -> 8) and restocked 1 (8 -> 9). Others stay at dispatched levels.
                Assert.Equal(9, (await afterR1.Set<InventoryBalance>().SingleAsync(b => b.OfferId == fixture.Offer2AId)).OnHand);
                Assert.Equal(9, (await afterR1.Set<InventoryBalance>().SingleAsync(b => b.OfferId == fixture.Offer2BId)).OnHand);
                Assert.Equal(7, (await afterR1.Set<InventoryBalance>().SingleAsync(b => b.OfferId == fixture.Offer1Id)).OnHand);
                Assert.Equal(7, (await afterR1.Set<StockBalance>().SingleAsync(s => s.SkuId == fixture.Sku1Id)).OnHand);

                var r1Row = await afterR1.Set<ReturnRequest>().SingleAsync(r => r.Id == return1.Id);
                Assert.Equal(ReturnStatus.AwaitingRefund, r1Row.Status);
                var refund1 = await afterR1.Set<Refund>().Include(r => r.Items).SingleAsync(r => r.ReturnId == return1.Id);
                Assert.Equal(so2.Id, refund1.SellerOrderId);
                Assert.Equal(0m, refund1.ShippingVnd);
                Assert.Equal(item2A.RefundFor(0, 1), refund1.AmountVnd);
                var refundItem1 = Assert.Single(refund1.Items);
                Assert.Equal((item2A.Id, 1, item2A.RefundFor(0, 1)), (refundItem1.OrderItemId, refundItem1.Quantity, refundItem1.AmountVnd));
            }

            // Buyer submits partial return #2 for the remaining 1 unit of Offer 2A; Seller 2 accepts 1 but restocks 0 (damaged).
            await fixture.Returns.Request(fixture.Buyer, orderId, so2.Id, "Second unit arrived scuffed", [new ReturnQuantity(item2A.Id, 1)]);
            var return2 = Assert.Single(await fixture.Returns.Queue(fixture.Seller2Owner, fixture.Seller2Id));
            var return2Line = Assert.Single(return2.Items);

            // Cannot request beyond purchased quantity (2 already returned/committed).
            await Assert.ThrowsAsync<ShopRuleException>(() =>
                fixture.Returns.Request(fixture.Buyer, orderId, so2.Id, "Over return", [new ReturnQuantity(item2A.Id, 1)]));

            await fixture.Returns.Review(fixture.Seller2Owner, fixture.Seller2Id, return2.Id, true, "Approved return 2");
            await fixture.Returns.Inspect(fixture.Seller2Owner, fixture.Seller2Id, return2.Id,
                [new InspectedItem(return2Line.Id, Accepted: 1, Restocked: 0)], "Damaged; not restocked");

            await using (var afterR2 = factory.CreateDbContext())
            {
                // OnHand stays 9 because Restocked == 0.
                Assert.Equal(9, (await afterR2.Set<InventoryBalance>().SingleAsync(b => b.OfferId == fixture.Offer2AId)).OnHand);
            }

            // Reconcile both pending refunds with SePay simulated refund gateway (Submit -> Pending -> advance clock -> Query -> Succeeded).
            var refundGateway = new SePayRefundGateway(new SePaySettings { SimulateRefunds = true });
            var workflow = new RefundWorkflow(fixture.Store, refundGateway, fixture.Clock);

            var dueSubmit = await workflow.DueRefunds();
            Assert.Equal(2, dueSubmit.Count);
            foreach (var rid in dueSubmit) await workflow.Reconcile(rid, CancellationToken.None);

            fixture.Clock.UtcNow = fixture.Clock.UtcNow.AddMinutes(2);
            var dueQuery = await workflow.DueRefunds();
            Assert.Equal(2, dueQuery.Count);
            foreach (var rid in dueQuery) await workflow.Reconcile(rid, CancellationToken.None);

            await using var verify = factory.CreateDbContext();
            var refunds = await verify.Set<Refund>().Where(r => r.OrderId == orderId).ToListAsync();
            Assert.Equal(2, refunds.Count);
            Assert.All(refunds, r =>
            {
                Assert.Equal(RefundStatus.Completed, r.Status);
                Assert.True(r.Simulated);
                Assert.StartsWith("SIM-", r.Reference);
            });

            var returns = await verify.Set<ReturnRequest>().Where(r => r.OrderId == orderId).ToListAsync();
            Assert.Equal(ReturnStatus.Rejected, returns.Single(r => r.Id == rejected.Id).Status);
            Assert.Equal(ReturnStatus.Completed, returns.Single(r => r.Id == return1.Id).Status);
            Assert.Equal(ReturnStatus.Completed, returns.Single(r => r.Id == return2.Id).Status);

            // Exact VND line reconciliation: Return 1 + Return 2 = Offer 2A net amount; Offer 2B & Seller 1 untouched.
            var item2ANet = item2A.UnitPriceVnd * item2A.Quantity - item2A.DiscountVnd;
            Assert.Equal(item2ANet, refunds.Sum(r => r.AmountVnd));
            Assert.True(refunds.Sum(r => r.AmountVnd) <= so2.TotalVnd);
            Assert.True(refunds.Sum(r => r.AmountVnd) <= preview.Total);
            Assert.Equal(2, await verify.Set<IntegrationOutboxEvent>().CountAsync(e => e.EventType == "RefundCompleted"));

            // Seller reports reflect refunded amounts strictly on Seller 2.
            var day = DateOnly.FromDateTime(ShopSettings.VietnamTime(fixture.Clock.GetUtcNow()).DateTime);
            var reporting = new ReportingService(fixture.Store);
            var s1Sales = await reporting.SellerSales(fixture.Seller1Owner, Seller.FirstPartyId, day, day);
            var s2Sales = await reporting.SellerSales(fixture.Seller2Owner, fixture.Seller2Id, day, day);
            Assert.Equal(0m, s1Sales.Refunded);
            Assert.Equal(item2ANet, s2Sales.Refunded);

            // Return requests after the 14-day SellerOrder delivery window are rejected.
            fixture.Clock.UtcNow = fixture.Clock.UtcNow.AddDays(15);
            await Assert.ThrowsAsync<ShopRuleException>(() =>
                fixture.Returns.Request(fixture.Buyer, orderId, so2.Id, "After window", [new ReturnQuantity(item2B.Id, 1)]));
        }
        finally
        {
            await context.Database.EnsureDeletedAsync();
        }
    }

    [SqlFact]
    public async Task DurableIdempotentRefundWorkflow_HandlesTransientFailureMismatchAndManualCompletion()
    {
        var factory = SqlTestDatabase.Create();
        await using var context = factory.CreateDbContext();
        try
        {
            await context.Database.MigrateAsync();
            var fixture = await SeedMarketplaceAsync(factory, context);

            await fixture.Cart.SetOffer(fixture.Buyer.UserId, null, fixture.Offer1Id, 1, add: false);
            await fixture.Cart.SetOffer(fixture.Buyer.UserId, null, fixture.Offer2BId, 1, add: false);

            var preview = await fixture.Checkout.Quote(fixture.Buyer, fixture.AddressId, PaymentMethod.SePay, "");
            var orderId = await fixture.Checkout.Place(fixture.Buyer, preview.Quote);
            var orderNumber = await context.Set<Order>().Where(o => o.Id == orderId).Select(o => o.Number).SingleAsync();
            Assert.True(await fixture.Payments.Apply(new GatewayOutcome(orderNumber, "SEPAY-DUR-1", preview.Total, "VND", true)));

            var so2 = await context.Set<SellerOrder>().AsNoTracking().SingleAsync(s => s.OrderId == orderId && s.SellerId == fixture.Seller2Id);
            await fixture.Orders.CancelSellerOrder(fixture.Seller2Owner, so2.Id, "Cannot fulfill", fixture.Seller2Id);

            // Idempotent queueing: re-invoking QueueSellerRefund with same business key adds no duplicate refund.
            await fixture.Store.Write(async db =>
            {
                var order = await db.First(db.Query<Order>().Where(o => o.Id == orderId)) ?? throw new InvalidOperationException();
                var child = await db.First(db.Query<SellerOrder>().Where(s => s.Id == so2.Id)) ?? throw new InvalidOperationException();
                var item = await db.First(db.Query<OrderItem>().Where(i => i.SellerOrderId == so2.Id)) ?? throw new InvalidOperationException();
                var res = await RefundWorkflow.QueueSellerRefund(db, order, child,
                    [new RefundAllocation(item.Id, item.Quantity, item.RefundFor(0, item.Quantity))],
                    child.ShippingVnd, $"cancel:{child.Id:N}", fixture.Clock.GetUtcNow());
                Assert.True(res);
                return 0;
            });
            Assert.Equal(1, await context.Set<Refund>().CountAsync(r => r.OrderId == orderId));

            var refundGateway = new StubRefundGateway();
            var workflow = new RefundWorkflow(fixture.Store, refundGateway, fixture.Clock);

            var due = await workflow.DueRefunds();
            var refundId = Assert.Single(due);
            var businessKey = await context.Set<Refund>().Where(r => r.Id == refundId).Select(r => r.BusinessKey).SingleAsync();

            // 1) Transient HttpRequestException on Submit -> committed Attempt=1, NextAttemptAt=+2m, safe to retry with same RequestKey.
            refundGateway.OnSubmit = _ => throw new HttpRequestException("Gateway timeout");
            await workflow.Reconcile(refundId, CancellationToken.None);

            await using (var v1 = factory.CreateDbContext())
            {
                var r1 = await v1.Set<Refund>().SingleAsync(r => r.Id == refundId);
                Assert.Equal(RefundStatus.Pending, r1.Status);
                Assert.Equal(1, r1.Attempts);
                Assert.Equal(fixture.Clock.UtcNow.AddMinutes(2), r1.NextAttemptAt);
                Assert.Contains("Provider outcome unknown", r1.Issue);
            }

            // 2) Retry Submit succeeds as Pending with ProviderReference -> uses identical RequestKey.
            fixture.Clock.UtcNow = fixture.Clock.UtcNow.AddMinutes(2);
            refundGateway.OnSubmit = req =>
            {
                Assert.Equal(businessKey, req.RequestKey);
                Assert.Equal("SEPAY-DUR-1", req.CaptureReference);
                Assert.Equal(so2.TotalVnd, req.AmountVnd);
                return new RefundGatewayOutcome(RefundGatewayStatus.Pending, "GW-REF-1", so2.TotalVnd, "Processing at provider");
            };
            await workflow.Reconcile(refundId, CancellationToken.None);

            // Admin cannot manually complete while automatic NextAttemptAt is still active.
            await Assert.ThrowsAsync<ShopRuleException>(() =>
                fixture.Payments.CompleteRefund(fixture.Admin, refundId, "GW-REF-1"));

            // 3) Next poll calls Query (not Submit) with same RequestKey; mismatched amount halts polling cleanly (NextAttemptAt = null).
            fixture.Clock.UtcNow = fixture.Clock.UtcNow.AddMinutes(2);
            refundGateway.OnSubmit = _ => throw new InvalidOperationException("Submit should not be called once ProviderReference exists.");
            refundGateway.OnQuery = (req, reference) =>
            {
                Assert.Equal(businessKey, req.RequestKey);
                Assert.Equal("GW-REF-1", reference);
                return new RefundGatewayOutcome(RefundGatewayStatus.Succeeded, reference, so2.TotalVnd - 1_000m);
            };
            await workflow.Reconcile(refundId, CancellationToken.None);

            await using (var v3 = factory.CreateDbContext())
            {
                var r3 = await v3.Set<Refund>().SingleAsync(r => r.Id == refundId);
                Assert.Equal(RefundStatus.Pending, r3.Status);
                Assert.Null(r3.NextAttemptAt);
                Assert.Equal("GW-REF-1", r3.ProviderReference);
                Assert.Contains("mismatched amount or reference", r3.Issue);
            }

            // 4) Admin reconciles manually: reference must match recorded ProviderReference; completes idempotently.
            await Assert.ThrowsAsync<ShopRuleException>(() =>
                fixture.Payments.CompleteRefund(fixture.Admin, refundId, "WRONG-REF"));

            await fixture.Payments.CompleteRefund(fixture.Admin, refundId, "GW-REF-1");
            await fixture.Payments.CompleteRefund(fixture.Admin, refundId, "GW-REF-1");

            await using var v4 = factory.CreateDbContext();
            var finalRefund = await v4.Set<Refund>().SingleAsync(r => r.Id == refundId);
            Assert.Equal(RefundStatus.Completed, finalRefund.Status);
            Assert.Equal("GW-REF-1", finalRefund.Reference);
            Assert.False(finalRefund.Simulated);
            Assert.Empty(finalRefund.Issue);
            Assert.Equal(1, await v4.Set<IntegrationOutboxEvent>().CountAsync(e => e.EventType == "RefundCompleted"));

            // Second seller cancellation produces a separate refund that cannot reuse an existing confirmed reference.
            var so1 = await v4.Set<SellerOrder>().AsNoTracking().SingleAsync(s => s.OrderId == orderId && s.SellerId == Seller.FirstPartyId);
            await fixture.Orders.CancelSellerOrder(fixture.Seller1Owner, so1.Id, "Seller 1 out of stock", Seller.FirstPartyId);
            var manualGateway = new SePayRefundGateway(new SePaySettings { SimulateRefunds = false });
            var manualWorkflow = new RefundWorkflow(fixture.Store, manualGateway, fixture.Clock);
            var so1RefundId = Assert.Single(await manualWorkflow.DueRefunds());
            await manualWorkflow.Reconcile(so1RefundId, CancellationToken.None);

            await Assert.ThrowsAsync<ShopRuleException>(() =>
                fixture.Payments.CompleteRefund(fixture.Admin, so1RefundId, "GW-REF-1"));
            await fixture.Payments.CompleteRefund(fixture.Admin, so1RefundId, "GW-REF-2");
        }
        finally
        {
            await context.Database.EnsureDeletedAsync();
        }
    }

    [SqlFact]
    public async Task UndeliverableDoubleCancellationAndCodReturn_ReconcileCouponStockAndSimulatedRefunds()
    {
        var factory = SqlTestDatabase.Create();
        await using var context = factory.CreateDbContext();
        try
        {
            await context.Database.MigrateAsync();
            var fixture = await SeedMarketplaceAsync(factory, context);

            // Part A: Paid two-seller order with SAVE50K where BOTH sellers independently cancel -> full refunds + coupon released!
            await fixture.Cart.SetOffer(fixture.Buyer.UserId, null, fixture.Offer1Id, 1, add: false);
            await fixture.Cart.SetOffer(fixture.Buyer.UserId, null, fixture.Offer2AId, 1, add: false);
            var q1 = await fixture.Checkout.Quote(fixture.Buyer, fixture.AddressId, PaymentMethod.SePay, "SAVE50K");
            var order1Id = await fixture.Checkout.Place(fixture.Buyer, q1.Quote);
            var order1Number = await context.Set<Order>().Where(o => o.Id == order1Id).Select(o => o.Number).SingleAsync();
            Assert.True(await fixture.Payments.Apply(new GatewayOutcome(order1Number, "SEPAY-DBL-1", q1.Total, "VND", true)));

            var o1Children = await context.Set<SellerOrder>().AsNoTracking().Where(s => s.OrderId == order1Id).ToListAsync();
            var o1S1 = o1Children.Single(s => s.SellerId == Seller.FirstPartyId);
            var o1S2 = o1Children.Single(s => s.SellerId == fixture.Seller2Id);

            await fixture.Orders.CancelSellerOrder(fixture.Seller1Owner, o1S1.Id, "Cancelled S1", Seller.FirstPartyId);
            Assert.Equal(ClaimStatus.Redeemed, (await context.Set<PromotionClaim>().AsNoTracking().SingleAsync(c => c.OrderId == order1Id)).Status);

            await fixture.Orders.CancelSellerOrder(fixture.Seller2Owner, o1S2.Id, "Cancelled S2", fixture.Seller2Id);
            await using (var verifyA = factory.CreateDbContext())
            {
                var o1 = await verifyA.Set<Order>().SingleAsync(o => o.Id == order1Id);
                Assert.Equal(OrderStatus.Cancelled, o1.Status);
                Assert.Equal(ClaimStatus.Released, (await verifyA.Set<PromotionClaim>().SingleAsync(c => c.OrderId == order1Id)).Status);

                var refunds = await verifyA.Set<Refund>().Where(r => r.OrderId == order1Id).ToListAsync();
                Assert.Equal(2, refunds.Count);
                Assert.Equal(q1.Total, refunds.Sum(r => r.AmountVnd));
            }

            // Part B: Two-seller SePay order -> Seller 1 marks shipped package Undeliverable (restocks 1 of 2 units); Seller 2 delivers.
            await fixture.Cart.SetOffer(fixture.Buyer.UserId, null, fixture.Offer1Id, 2, add: false);
            await fixture.Cart.SetOffer(fixture.Buyer.UserId, null, fixture.Offer2BId, 1, add: false);
            var q2 = await fixture.Checkout.Quote(fixture.Buyer, fixture.AddressId, PaymentMethod.SePay, "SAVE50K");
            var order2Id = await fixture.Checkout.Place(fixture.Buyer, q2.Quote);
            var order2Number = await context.Set<Order>().Where(o => o.Id == order2Id).Select(o => o.Number).SingleAsync();
            Assert.True(await fixture.Payments.Apply(new GatewayOutcome(order2Number, "SEPAY-UNDEL-2", q2.Total, "VND", true)));

            var o2Children = await context.Set<SellerOrder>().AsNoTracking().Where(s => s.OrderId == order2Id).ToListAsync();
            var o2S1 = o2Children.Single(s => s.SellerId == Seller.FirstPartyId);
            var o2S2 = o2Children.Single(s => s.SellerId == fixture.Seller2Id);
            var o2Item1 = await context.Set<OrderItem>().AsNoTracking().SingleAsync(i => i.SellerOrderId == o2S1.Id);

            await fixture.Orders.StartPackingSellerOrder(fixture.Seller1Owner, o2S1.Id, Seller.FirstPartyId);
            await fixture.Orders.DispatchSellerOrder(fixture.Seller1Owner, o2S1.Id, "VNPost", "VNP-9", Seller.FirstPartyId);
            await fixture.Orders.DeliveryExceptionSellerOrder(fixture.Seller1Owner, o2S1.Id, "Buyer unreachable", Seller.FirstPartyId);
            await fixture.Orders.ReceiveUndeliveredSellerOrder(fixture.Seller1Owner, o2S1.Id, "Returned by carrier",
                [new ReturnQuantity(o2Item1.Id, 1)], Seller.FirstPartyId);

            await fixture.Orders.StartPackingSellerOrder(fixture.Seller2Owner, o2S2.Id, fixture.Seller2Id);
            await fixture.Orders.DispatchSellerOrder(fixture.Seller2Owner, o2S2.Id, "GHN", "GHN-9", fixture.Seller2Id);
            await fixture.Orders.DeliverSellerOrder(fixture.Seller2Owner, o2S2.Id, null, "", fixture.Seller2Id);

            await using (var verifyB = factory.CreateDbContext())
            {
                var o2 = await verifyB.Set<Order>().SingleAsync(o => o.Id == order2Id);
                Assert.Equal(OrderStatus.Delivered, o2.Status);
                // Offer 1 started with 10, dispatched 2 (8), undeliverable restocked 1 -> OnHand 9
                Assert.Equal((9, 0), ((await verifyB.Set<InventoryBalance>().SingleAsync(b => b.OfferId == fixture.Offer1Id)).OnHand,
                    (await verifyB.Set<InventoryBalance>().SingleAsync(b => b.OfferId == fixture.Offer1Id)).Reserved));
                Assert.Equal((9, 0), ((await verifyB.Set<StockBalance>().SingleAsync(s => s.SkuId == fixture.Sku1Id)).OnHand,
                    (await verifyB.Set<StockBalance>().SingleAsync(s => s.SkuId == fixture.Sku1Id)).Reserved));
                var undelRefund = await verifyB.Set<Refund>().SingleAsync(r => r.OrderId == order2Id);
                Assert.Equal(o2S1.Id, undelRefund.SellerOrderId);
                Assert.Equal(o2S1.TotalVnd, undelRefund.AmountVnd);
                Assert.Equal(o2S1.ShippingVnd, undelRefund.ShippingVnd);
            }

            // Part C: Single-seller First-Party COD order -> Delivered -> Partial Return -> Admin records simulated COD refund.
            await fixture.Cart.SetOffer(fixture.Buyer.UserId, null, fixture.Offer1Id, 2, add: false);
            var q3 = await fixture.Checkout.Quote(fixture.Buyer, fixture.AddressId, PaymentMethod.Cod, "");
            var order3Id = await fixture.Checkout.Place(fixture.Buyer, q3.Quote);
            await fixture.Orders.StartPacking(fixture.Staff, order3Id);
            await fixture.Orders.Dispatch(fixture.Staff, order3Id, "GHN", "COD-TRK-1");
            await fixture.Orders.Deliver(fixture.Staff, order3Id, q3.Total, "COD-COLLECTED-1");

            var o3Item = await context.Set<OrderItem>().AsNoTracking().SingleAsync(i => i.OrderId == order3Id);
            await fixture.Returns.Request(fixture.Buyer, order3Id, "Return 1 of COD order", [new ReturnQuantity(o3Item.Id, 1)]);
            var codReturn = Assert.Single(await fixture.Returns.Queue(fixture.Staff));
            await fixture.Returns.Review(fixture.Staff, codReturn.Id, true, "OK");
            var codDetails = Assert.IsType<OrderDetails>(await fixture.Orders.Details(fixture.Staff, order3Id, adminView: true));
            var codReturnLine = Assert.Single(Assert.Single(codDetails.Returns).Items);
            await fixture.Returns.Inspect(fixture.Staff, codReturn.Id, [new InspectedItem(codReturnLine.Id, 1, 1)], "Good condition");

            var pending = await fixture.Payments.PendingRefunds(fixture.Admin);
            var codRefund = Assert.Single(pending, r => r.OrderId == order3Id);
            Assert.Null(codRefund.NextAttemptAt);
            Assert.Contains("COD", codRefund.Issue);

            await fixture.Payments.CompleteRefund(fixture.Admin, codRefund.Id, "BANK-COD-REF-1");

            await using var verifyC = factory.CreateDbContext();
            var doneCodRefund = await verifyC.Set<Refund>().SingleAsync(r => r.Id == codRefund.Id);
            Assert.Equal(RefundStatus.Completed, doneCodRefund.Status);
            Assert.True(doneCodRefund.Simulated);
            Assert.Equal("BANK-COD-REF-1", doneCodRefund.Reference);
            Assert.Equal(ReturnStatus.Completed, (await verifyC.Set<ReturnRequest>().SingleAsync(r => r.Id == codReturn.Id)).Status);
        }
        finally
        {
            await context.Database.EnsureDeletedAsync();
        }
    }

    [SqlFact]
    public async Task Phase3ToPhase4Migration_BackfillsShipmentsReturnsAndRefunds()
    {
        var factory = SqlTestDatabase.Create();
        await using var context = factory.CreateDbContext();
        try
        {
            await context.GetService<IMigrator>().MigrateAsync("20261113071706_MarketplaceCheckoutAndPayments");

            var buyer = await CreateUserAsync(context, "p3-buyer");
            var category = new Category { Name = "Electronics", Active = true };
            context.Add(category);
            await context.SaveChangesAsync();

            var sku = new Sku { Code = "SKU-P3", Size = "L", Color = "Blue", Active = true, PriceVnd = 150_000m };
            var product = new Product
            {
                CategoryId = category.Id, Name = "Smart Watch", Slug = "smart-watch", Description = "Desc",
                Skus = [sku], Images = [new ProductImage { FileName = "watch.webp", Alt = "Watch" }]
            };
            product.Publish(true);
            context.Add(product);
            await context.SaveChangesAsync();

            var offerId = await context.Set<SellerOffer>()
                .Where(o => o.SellerId == Seller.FirstPartyId && o.SkuId == sku.Id)
                .Select(o => o.Id)
                .SingleAsync();

            var deliveredOrderId = Guid.NewGuid();
            var deliveredSellerOrderId = Guid.NewGuid();
            var deliveredAttemptId = Guid.NewGuid();
            var returnId = Guid.NewGuid();
            var cancelledOrderId = Guid.NewGuid();
            var cancelledSellerOrderId = Guid.NewGuid();
            var cancelledAttemptId = Guid.NewGuid();
            var now = new DateTimeOffset(2026, 11, 13, 8, 0, 0, TimeSpan.Zero);
            var deliveredAt = now.AddHours(2);

            await context.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT dbo.Orders (Id, SellerId, Number, CustomerId, CustomerEmail, SubmissionKey, CreatedAt, DeliveredAt, Status, PaymentStatus, Recipient, Phone, ShippingAddress, ShippingVnd, TotalVnd)
                VALUES
                    ({deliveredOrderId}, 1, N'HKP3DELIVERED', {buyer.UserId}, {buyer.Email}, {Guid.NewGuid()}, {now}, {deliveredAt}, {(int)OrderStatus.Delivered}, {(int)PaymentStatus.Paid}, N'Buyer', N'0901', N'Addr', 30000, 330000),
                    ({cancelledOrderId}, 1, N'HKP3CANCELLED', {buyer.UserId}, {buyer.Email}, {Guid.NewGuid()}, {now}, NULL, {(int)OrderStatus.Cancelled}, {(int)PaymentStatus.Paid}, N'Buyer', N'0901', N'Addr', 30000, 180000);

                INSERT dbo.SellerOrders (Id, OrderId, SellerId, Number, Status, SubtotalVnd, SellerDiscountVnd, PlatformDiscountVnd, ShippingVnd, TotalVnd, CommissionVnd, CreatedAt, ConfirmedAt, CancelledAt)
                VALUES
                    ({deliveredSellerOrderId}, {deliveredOrderId}, 1, N'HKP3DELIVERED-S1', {(int)OrderStatus.Delivered}, 300000, 0, 0, 30000, 330000, 0, {now}, {now}, NULL),
                    ({cancelledSellerOrderId}, {cancelledOrderId}, 1, N'HKP3CANCELLED-S1', {(int)OrderStatus.Cancelled}, 150000, 0, 0, 30000, 180000, 0, {now}, {now}, {now});

                INSERT dbo.OrderItems (OrderId, SellerOrderId, SellerId, SkuId, OfferId, SkuCode, SellerSkuCode, Name, ProductAndVariantSnapshot, Quantity, UnitPriceVnd, DiscountVnd, SellerDiscountVnd, PlatformDiscountVnd, CommissionBps, CommissionVnd)
                VALUES
                    ({deliveredOrderId}, {deliveredSellerOrderId}, 1, {sku.Id}, {offerId}, N'SKU-P3', N'SKU-P3', N'Smart Watch', N'Smart Watch / L / Blue', 2, 150000, 0, 0, 0, 0, 0),
                    ({cancelledOrderId}, {cancelledSellerOrderId}, 1, {sku.Id}, {offerId}, N'SKU-P3', N'SKU-P3', N'Smart Watch', N'Smart Watch / L / Blue', 1, 150000, 0, 0, 0, 0, 0);

                DECLARE @deliveredItemId bigint = (SELECT TOP(1) Id FROM dbo.OrderItems WHERE OrderId = {deliveredOrderId});

                INSERT dbo.Payments (OrderId, Method, Status, AmountVnd, PaidAt, ProviderTransaction, CollectionReference, Issue)
                VALUES
                    ({deliveredOrderId}, 1, {(int)PaymentStatus.Paid}, 330000, {now}, N'TX-P3-1', N'', N''),
                    ({cancelledOrderId}, 1, {(int)PaymentStatus.Paid}, 180000, {now}, N'TX-P3-2', N'', N'');

                INSERT dbo.PaymentAttempts (Id, OrderId, AttemptNumber, Method, Status, AmountVnd, CapturedAmountVnd, ProviderInvoice, ProviderTransaction, AppliedToOrder, CreatedAt, PaidAt, Issue)
                VALUES
                    ({deliveredAttemptId}, {deliveredOrderId}, 1, 1, {(int)PaymentStatus.Paid}, 330000, 330000, N'HKP3DELIVERED', N'TX-P3-1', 1, {now}, {now}, N''),
                    ({cancelledAttemptId}, {cancelledOrderId}, 1, 1, {(int)PaymentStatus.Paid}, 180000, 180000, N'HKP3CANCELLED', N'TX-P3-2', 1, {now}, {now}, N'');

                INSERT dbo.Shipments (OrderId, Carrier, TrackingNumber, DispatchedAt)
                VALUES ({deliveredOrderId}, N'GHN', N'P3-TRACK-1', {now});

                INSERT dbo.ReturnRequests (Id, OrderId, Status, Reason, StaffNote, RequestedAt)
                VALUES ({returnId}, {deliveredOrderId}, {(int)ReturnStatus.Completed}, N'Too large', N'Approved', {deliveredAt});

                INSERT dbo.ReturnItems (ReturnId, OrderItemId, Requested, Accepted, Restocked)
                VALUES ({returnId}, @deliveredItemId, 1, 1, 1);

                INSERT dbo.Refunds (OrderId, ReturnId, BusinessKey, AmountVnd, Status, Reference, RequestedAt, CompletedAt)
                VALUES
                    ({deliveredOrderId}, {returnId}, CONCAT(REPLACE(CONVERT(nvarchar(36), {deliveredOrderId}), N'-', N''), N':return'), 150000, {(int)RefundStatus.Completed}, N'SIM-LEGACY-1', {deliveredAt}, {deliveredAt}),
                    ({cancelledOrderId}, NULL, CONCAT(REPLACE(CONVERT(nvarchar(36), {cancelledOrderId}), N'-', N''), N':cancelled'), 180000, {(int)RefundStatus.Pending}, N'', {now}, NULL);
                """);

            await context.Database.MigrateAsync();

            await using var verify = factory.CreateDbContext();
            var delSo = await verify.Set<SellerOrder>().SingleAsync(s => s.Id == deliveredSellerOrderId);
            Assert.Equal(deliveredAt, delSo.DeliveredAt);

            var shipment = await verify.Set<Shipment>().SingleAsync(s => s.OrderId == deliveredOrderId);
            Assert.Equal(deliveredSellerOrderId, shipment.SellerOrderId);

            var ret = await verify.Set<ReturnRequest>().Include(r => r.Items).SingleAsync(r => r.Id == returnId);
            Assert.Equal(deliveredSellerOrderId, ret.SellerOrderId);
            Assert.Equal(deliveredSellerOrderId, Assert.Single(ret.Items).SellerOrderId);

            var returnRefund = await verify.Set<Refund>().SingleAsync(r => r.OrderId == deliveredOrderId);
            Assert.Equal(deliveredSellerOrderId, returnRefund.SellerOrderId);
            Assert.Equal(deliveredAttemptId, returnRefund.PaymentAttemptId);
            Assert.True(returnRefund.Simulated);
            Assert.Equal(0m, returnRefund.ShippingVnd);

            var cancelRefund = await verify.Set<Refund>().SingleAsync(r => r.OrderId == cancelledOrderId);
            Assert.Equal(cancelledSellerOrderId, cancelRefund.SellerOrderId);
            Assert.Equal(cancelledAttemptId, cancelRefund.PaymentAttemptId);
            Assert.Equal(30_000m, cancelRefund.ShippingVnd);
            Assert.False(cancelRefund.Simulated);

            // Composite FK guard prevents attaching a ReturnItem to a different SellerOrderId than its ReturnRequest.
            await Assert.ThrowsAsync<SqlException>(() => verify.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT dbo.ReturnItems (ReturnId, SellerOrderId, OrderItemId, Requested, Accepted, Restocked)
                VALUES ({returnId}, {cancelledSellerOrderId}, (SELECT TOP(1) Id FROM dbo.OrderItems WHERE OrderId = {cancelledOrderId}), 1, 0, 0);
                """));
        }
        finally
        {
            await context.Database.EnsureDeletedAsync();
        }
    }

    private sealed record MarketplaceFixture(
        IShopStore Store,
        TestClock Clock,
        ShopActor Admin,
        ShopActor Staff,
        ShopActor Buyer,
        ShopActor Seller1Owner,
        ShopActor Seller2Owner,
        long AddressId,
        long Seller2Id,
        long Sku1Id,
        long Sku2Id,
        long Offer1Id,
        long Offer2AId,
        long Offer2BId,
        CartService Cart,
        CheckoutService Checkout,
        OrderService Orders,
        ReturnService Returns,
        PaymentService Payments);

    private static async Task<MarketplaceFixture> SeedMarketplaceAsync(SqlTestDatabase factory, ShopDbContext context)
    {
        var clock = new TestClock();
        var store = new SqlShopStore(factory);
        var settings = new ShopSettings { FlatShippingVnd = 30_000m, DefaultCommissionBps = 1000, PublicOrigin = "https://shop.example.test" };

        var admin = await CreateUserAsync(context, "admin", admin: true, staff: true);
        var staff = await CreateUserAsync(context, "staff", staff: true);
        var buyer = await CreateUserAsync(context, "buyer");
        var seller1Owner = await CreateUserAsync(context, "seller1-owner");
        var seller2Owner = await CreateUserAsync(context, "seller2-owner");

        var address = new CustomerAddress
        {
            CustomerId = buyer.UserId,
            Recipient = "Nguyen Buyer",
            Phone = "0901234567",
            AddressText = "123 Le Loi, District 1"
        };
        context.Add(address);

        var category = new Category { Name = "Apparel", Active = true };
        context.Add(category);
        await context.SaveChangesAsync();

        var sku1 = new Sku { Code = "SKU-1", Size = "L", Color = "Blue", Active = true, PriceVnd = 200_000m };
        var product1 = new Product
        {
            CategoryId = category.Id, Name = "Classic Tee", Slug = "classic-tee", Description = "Tee",
            Skus = [sku1], Images = [new ProductImage { FileName = "p1.webp", Alt = "P1" }]
        };
        product1.Publish(true);

        var sku2 = new Sku { Code = "SKU-2", Size = "M", Color = "Black", Active = true, PriceVnd = null };
        var product2 = new Product
        {
            CategoryId = category.Id, Name = "Seller Cap", Slug = "seller-cap", Description = "Cap",
            Skus = [sku2], Images = [new ProductImage { FileName = "p2.webp", Alt = "P2" }]
        };
        product2.Publish(true);
        context.AddRange(product1, product2);
        await context.SaveChangesAsync();

        var inventory = new InventoryService(store, clock);
        await inventory.Adjust(staff, sku1.Id, 10, 1, "Initial stock");

        var sellers = new SellerService(store, clock);
        var seller2Id = await sellers.Register(seller2Owner, "Partner Store", "partner-store");
        await sellers.SetStatus(admin, seller2Id, SellerStatus.Active, null);

        var loc2 = new SellerLocation { SellerId = seller2Id, Name = "Partner Warehouse", Active = true };
        context.Add(loc2);
        await context.SaveChangesAsync();

        var offer1 = await context.Set<SellerOffer>().SingleAsync(o => o.SellerId == Seller.FirstPartyId && o.SkuId == sku1.Id);
        var offer2a = new SellerOffer { SellerId = seller2Id, SkuId = sku1.Id, SellerSkuCode = "P2-SKU1", PriceVnd = 180_000m, Status = SellerOfferStatus.Approved };
        var offer2b = new SellerOffer { SellerId = seller2Id, SkuId = sku2.Id, SellerSkuCode = "P2-SKU2", PriceVnd = 100_000m, Status = SellerOfferStatus.Approved };
        context.AddRange(offer2a, offer2b);
        await context.SaveChangesAsync();

        context.AddRange(
            new InventoryBalance { SellerId = seller2Id, OfferId = offer2a.Id, LocationId = loc2.Id, OnHand = 10, Reserved = 0 },
            new InventoryBalance { SellerId = seller2Id, OfferId = offer2b.Id, LocationId = loc2.Id, OnHand = 10, Reserved = 0 },
            new SellerMembership { SellerId = Seller.FirstPartyId, UserId = seller1Owner.UserId, Role = SellerRole.Owner, Status = SellerMembershipStatus.Active },
            new Promotion
            {
                Code = "SAVE50K", Active = true, IsPercentage = false, Value = 50_000m, MinimumVnd = 100_000m,
                UsageCap = 10, StartsAt = clock.GetUtcNow().AddDays(-1), EndsAt = clock.GetUtcNow().AddDays(1)
            });
        await context.SaveChangesAsync();

        var callbackPort = new HKShop.Commerce.Application.Ports.ShippingCommerceCallbackAdapter();
        var dummyRouting = new DummyTransportRouting();
        var transportService = new HKShop.Application.Transportation.TransportService(store, clock, dummyRouting, callbackPort);
        var shippingGateway = new HKShop.EOffice.Shipping.Application.ShippingGatewayAdapter(
            transportService, new HKShop.Application.Transportation.TransportSettings(), dummyRouting);

        return new MarketplaceFixture(
            store, clock, admin, staff, buyer, seller1Owner, seller2Owner,
            address.Id, seller2Id, sku1.Id, sku2.Id, offer1.Id, offer2a.Id, offer2b.Id,
            new CartService(store, clock),
            new CheckoutService(store, settings, clock, shipping: shippingGateway),
            new OrderService(store, clock, shippingGateway),
            new ReturnService(store, clock, shippingGateway),
            new PaymentService(store, new DummyPaymentGateway(), clock));
    }

    private sealed class DummyTransportRouting : HKShop.Application.Transportation.ITransportRouting
    {
        public Task<long?> Distance(HKShop.Application.Transportation.TransportPoint origin, HKShop.Application.Transportation.TransportPoint destination, CancellationToken token = default) =>
            Task.FromResult<long?>(1000);
    }

    private static async Task<ShopActor> CreateUserAsync(ShopDbContext context, string name, bool admin = false, bool staff = false)
    {
        var email = $"{name}-{Guid.NewGuid():N}@example.test";
        var user = new ShopUser
        {
            Email = email, UserName = email, NormalizedEmail = email.ToUpperInvariant(),
            NormalizedUserName = email.ToUpperInvariant(), EmailConfirmed = true, TwoFactorEnabled = true
        };
        context.Add(user);
        await context.SaveChangesAsync();
        return new ShopActor(user.Id, email, true, staff, admin) { Mfa = true };
    }

    private sealed class DummyPaymentGateway : IPaymentGateway
    {
        public bool Available => true;
        public HostedCheckout BuildCheckout(string invoice, Guid order, decimal amount) => new("https://pay.example.test", []);
        public Task<GatewayOutcome?> Query(string invoice, CancellationToken cancellationToken) => Task.FromResult<GatewayOutcome?>(null);
    }

    private sealed class StubRefundGateway : IRefundGateway
    {
        public Func<RefundGatewayRequest, RefundGatewayOutcome>? OnSubmit { get; set; }
        public Func<RefundGatewayRequest, string, RefundGatewayOutcome>? OnQuery { get; set; }

        public Task<RefundGatewayOutcome> Submit(RefundGatewayRequest request, CancellationToken cancellationToken) =>
            Task.FromResult((OnSubmit ?? throw new InvalidOperationException("OnSubmit not configured."))(request));

        public Task<RefundGatewayOutcome> Query(RefundGatewayRequest request, string reference, CancellationToken cancellationToken) =>
            Task.FromResult((OnQuery ?? throw new InvalidOperationException("OnQuery not configured."))(request, reference));
    }

    private sealed class TestClock : TimeProvider
    {
        public DateTimeOffset UtcNow { get; set; } = new(2026, 11, 13, 9, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => UtcNow;
    }
}
