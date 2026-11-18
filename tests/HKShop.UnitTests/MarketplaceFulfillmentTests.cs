using HKShop.Application.Payments;
using HKShop.Domain.Orders;
using HKShop.Infrastructure.Payments;
using Xunit;

namespace HKShop.UnitTests;

public sealed class MarketplaceFulfillmentTests
{
    [Theory]
    [InlineData(3, 100_000, 10_000, 1, 2)]
    [InlineData(4, 50_000, 5_001, 1, 3)]
    public void OrderItem_RefundFor_MultiStepPartialReturnsSumToNetLineAmount(
        int totalQty, decimal unitPriceVnd, decimal discountVnd, int firstReturnQty, int secondReturnQty)
    {
        var item = new OrderItem
        {
            Quantity = totalQty,
            UnitPriceVnd = unitPriceVnd,
            DiscountVnd = discountVnd
        };

        var firstRefund = item.RefundFor(0, firstReturnQty);
        var secondRefund = item.RefundFor(firstReturnQty, secondReturnQty);
        var expectedNet = unitPriceVnd * totalQty - discountVnd;

        Assert.Equal(expectedNet, firstRefund + secondRefund);
        Assert.Equal(expectedNet, item.RefundFor(0, totalQty));
    }

    [Fact]
    public async Task SePayRefundGateway_WhenSimulationDisabled_RequiresManualReview()
    {
        var gateway = new SePayRefundGateway(new SePaySettings { SimulateRefunds = false });
        var request = new RefundGatewayRequest("order:return:1", "SEPAY-TX-1", 150_000m);

        var submit = await gateway.Submit(request, CancellationToken.None);

        Assert.Equal(RefundGatewayStatus.NeedsReview, submit.Status);
        Assert.Equal("", submit.Reference);
        Assert.Equal(150_000m, submit.AmountVnd);
        Assert.False(submit.Simulated);
        Assert.NotEmpty(submit.Issue);
    }

    [Fact]
    public async Task SePayRefundGateway_WhenSimulationEnabled_SubmitsDeterministicPendingReferenceThenConfirms()
    {
        var gateway = new SePayRefundGateway(new SePaySettings { SimulateRefunds = true });
        var request = new RefundGatewayRequest("order:return:2", "SEPAY-TX-2", 200_000m);

        var submit1 = await gateway.Submit(request, CancellationToken.None);
        var submit2 = await gateway.Submit(request, CancellationToken.None);

        Assert.Equal(RefundGatewayStatus.Pending, submit1.Status);
        Assert.StartsWith("SIM-", submit1.Reference);
        Assert.Equal(submit1.Reference, submit2.Reference);

        var matchingQuery = await gateway.Query(request, submit1.Reference, CancellationToken.None);
        Assert.Equal(RefundGatewayStatus.Succeeded, matchingQuery.Status);
        Assert.True(matchingQuery.Simulated);
        Assert.Equal(submit1.Reference, matchingQuery.Reference);
        Assert.Equal(200_000m, matchingQuery.AmountVnd);

        var mismatchedQuery = await gateway.Query(request, "EXT-REF-999", CancellationToken.None);
        Assert.Equal(RefundGatewayStatus.NeedsReview, mismatchedQuery.Status);
        Assert.False(mismatchedQuery.Simulated);
        Assert.Equal("EXT-REF-999", mismatchedQuery.Reference);
    }

    [Fact]
    public void SellerOrder_ChangeStatus_PreservesFirstTerminalAndMilestoneTimestamps()
    {
        var t0 = new DateTimeOffset(2026, 11, 13, 9, 0, 0, TimeSpan.Zero);
        var so = new SellerOrder { CreatedAt = t0, Status = OrderStatus.AwaitingPayment };

        so.ChangeStatus(OrderStatus.Confirmed, t0.AddMinutes(5));
        so.ChangeStatus(OrderStatus.Packing, t0.AddMinutes(10));
        so.ChangeStatus(OrderStatus.Confirmed, t0.AddMinutes(15));
        Assert.Equal(t0.AddMinutes(5), so.ConfirmedAt);
        Assert.Null(so.DeliveredAt);
        Assert.Null(so.CancelledAt);

        so.ChangeStatus(OrderStatus.Shipped, t0.AddMinutes(20));
        so.ChangeStatus(OrderStatus.Delivered, t0.AddMinutes(30));
        so.ChangeStatus(OrderStatus.Delivered, t0.AddMinutes(40));
        Assert.Equal(t0.AddMinutes(30), so.DeliveredAt);

        var cancelled = new SellerOrder { CreatedAt = t0, Status = OrderStatus.Confirmed };
        cancelled.ChangeStatus(OrderStatus.Cancelled, t0.AddMinutes(8));
        cancelled.ChangeStatus(OrderStatus.Cancelled, t0.AddMinutes(12));
        Assert.Equal(t0.AddMinutes(8), cancelled.CancelledAt);
    }
}
