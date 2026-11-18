using HKShop.Application;
using HKShop.Application.Checkout;
using HKShop.Domain;
using HKShop.Domain.Orders;
using HKShop.Domain.Sellers;
using Xunit;

namespace HKShop.UnitTests;

public sealed class MarketplaceCheckoutTests
{
    [Fact]
    public void Allocate_TwoSellers_ChargesPerSellerShippingAndSnapshotsThirdPartyCommission()
    {
        List<CartLine> lines =
        [
            new(10, 100, Seller.FirstPartyId, "HKShop", "HK-100", "item-1", "Item 1", 1, 5, 200_000m, true, false),
            new(20, 101, 2, "Partner Store", "P-101", "item-2", "Item 2", 2, 5, 100_000m, true, false)
        ];

        var sellers = CheckoutService.Allocate(lines, platformDiscount: 0m, flatShippingVnd: 30_000m, defaultCommissionBps: 1000);

        Assert.Equal(2, sellers.Count);

        var firstParty = sellers[0];
        Assert.Equal(Seller.FirstPartyId, firstParty.SellerId);
        Assert.Equal(200_000m, firstParty.SubtotalVnd);
        Assert.Equal(30_000m, firstParty.ShippingVnd);
        Assert.Equal(0m, firstParty.CommissionVnd);
        Assert.Equal(230_000m, firstParty.TotalVnd);
        Assert.Equal(0, Assert.Single(firstParty.Lines).CommissionBps);

        var thirdParty = sellers[1];
        Assert.Equal(2, thirdParty.SellerId);
        Assert.Equal(200_000m, thirdParty.SubtotalVnd);
        Assert.Equal(30_000m, thirdParty.ShippingVnd);
        Assert.Equal(20_000m, thirdParty.CommissionVnd);
        Assert.Equal(230_000m, thirdParty.TotalVnd);
        Assert.Equal(1000, Assert.Single(thirdParty.Lines).CommissionBps);
        Assert.Equal(20_000m, Assert.Single(thirdParty.Lines).CommissionVnd);
    }

    [Fact]
    public void Allocate_PlatformCouponAndOddCommission_DistributesRemaindersWithoutDrift()
    {
        List<CartLine> lines =
        [
            new(11, 201, Seller.FirstPartyId, "HKShop", "HK-201", "p1", "P1", 1, 10, 1_000m, true, false),
            new(21, 202, 2, "Shop Two", "S2-A", "p2", "P2", 1, 10, 155m, true, false),
            new(22, 203, 2, "Shop Two", "S2-B", "p3", "P3", 1, 10, 155m, true, false)
        ];

        var sellers = CheckoutService.Allocate(lines, platformDiscount: 101m, flatShippingVnd: 30_000m, defaultCommissionBps: 1000);

        var allLines = sellers.SelectMany(s => s.Lines).ToList();
        Assert.Equal(101m, allLines.Sum(l => l.PlatformDiscountVnd));
        Assert.Equal(101m, sellers.Sum(s => s.PlatformDiscountVnd));

        var thirdParty = sellers[1];
        Assert.Equal(310m, thirdParty.SubtotalVnd);
        Assert.Equal(31m, thirdParty.CommissionVnd);
        Assert.Equal(31m, thirdParty.Lines.Sum(l => l.CommissionVnd));
        Assert.Equal([16m, 15m], thirdParty.Lines.Select(l => l.CommissionVnd).ToArray());
    }

    [Fact]
    public void CartView_SupportsCod_OnlyForSingleFirstPartySellerCarts()
    {
        var firstPartyOnly = new CartView(Guid.NewGuid(), [
            new(1, 10, Seller.FirstPartyId, "HKShop", "A", "a", "A", 1, 5, 100_000m, true, false)
        ]);
        var thirdPartyOnly = new CartView(Guid.NewGuid(), [
            new(2, 10, 2, "Shop Two", "B", "b", "B", 1, 5, 100_000m, true, false)
        ]);
        var mixed = new CartView(Guid.NewGuid(), [
            new(1, 10, Seller.FirstPartyId, "HKShop", "A", "a", "A", 1, 5, 100_000m, true, false),
            new(2, 11, 2, "Shop Two", "B", "b", "B", 1, 5, 100_000m, true, false)
        ]);
        var empty = new CartView(Guid.NewGuid(), []);

        Assert.True(firstPartyOnly.SupportsCod);
        Assert.False(thirdPartyOnly.SupportsCod);
        Assert.False(mixed.SupportsCod);
        Assert.False(empty.SupportsCod);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(10_001)]
    public void ShopRules_CommissionBps_RejectsOutOfRangeValues(int bps)
    {
        Assert.Throws<ShopRuleException>(() => ShopRules.CommissionBps(bps));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1000)]
    [InlineData(10_000)]
    public void ShopRules_CommissionBps_AcceptsValidRange(int bps)
    {
        Assert.Equal(bps, ShopRules.CommissionBps(bps));
    }
}
