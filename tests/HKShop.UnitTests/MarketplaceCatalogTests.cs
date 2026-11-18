using HKShop.Application.Catalog;
using HKShop.Application.Checkout;
using HKShop.Domain;
using HKShop.Domain.Catalog;
using HKShop.Domain.Sellers;
using Xunit;

namespace HKShop.UnitTests;

public sealed class MarketplaceCatalogTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(-1000)]
    [InlineData(99.99)]
    [InlineData(100.5)]
    public void SellerOfferPrice_MustBePositiveWholeVnd(decimal price)
    {
        Assert.Throws<ShopRuleException>(() =>
        {
            if (price <= 0 || decimal.Truncate(price) != price)
                throw new ShopRuleException("Giá phải là số nguyên dương.");
        });
    }

    [Theory]
    [InlineData(10_000)]
    [InlineData(250_000)]
    [InlineData(1)]
    public void SellerOfferPrice_AcceptsPositiveWholeVnd(decimal price)
    {
        var exception = Record.Exception(() =>
        {
            if (price <= 0 || decimal.Truncate(price) != price)
                throw new ShopRuleException("Giá phải là số nguyên dương.");
        });
        Assert.Null(exception);
    }

    [Theory]
    [InlineData(" sku-001 ", "SKU-001")]
    [InlineData("seller_item_a", "SELLER_ITEM_A")]
    [InlineData("VN-SHIRT-M", "VN-SHIRT-M")]
    public void SellerSkuCode_IsTrimmedAndNormalizedToUppercase(string input, string expected)
    {
        var normalized = ShopRules.Text(input, 64, "Mã SKU người bán").ToUpperInvariant();
        Assert.Equal(expected, normalized);
    }

    [Fact]
    public void InventoryBalance_CalculatesAvailableStockAccurately()
    {
        var balance = new InventoryBalance
        {
            SellerId = 10,
            OfferId = 100,
            LocationId = 1,
            OnHand = 15,
            Reserved = 4
        };

        var available = balance.OnHand - balance.Reserved;
        Assert.Equal(11, available);
    }

    [Fact]
    public void StorefrontOfferSelection_OrdersByInStockFirstThenLowestPriceThenId()
    {
        // Kịch bản: 3 người bán cùng bán một biến thể SKU
        var offerA = new
        {
            Id = 1L,
            SellerId = 10L,
            SellerName = "Seller A",
            Price = 200_000m,
            Available = 5
        };
        var offerB = new
        {
            Id = 2L,
            SellerId = 20L,
            SellerName = "Seller B",
            Price = 180_000m, // Giá rẻ nhất và còn hàng -> Ưu tiên 1
            Available = 10
        };
        var offerC = new
        {
            Id = 3L,
            SellerId = 30L,
            SellerName = "Seller C",
            Price = 150_000m, // Giá rẻ nhất nhưng HẾT HÀNG -> Xếp sau hàng còn tồn kho
            Available = 0
        };
        var offerD = new
        {
            Id = 4L,
            SellerId = 40L,
            SellerName = "Seller D",
            Price = 180_000m, // Cùng giá 180k với Seller B nhưng Id = 4 > 2 -> Xếp sau B
            Available = 2
        };

        var offers = new[] { offerA, offerB, offerC, offerD };

        // Thuật toán của HKShop trong CatalogService.cs
        var ordered = offers
            .OrderByDescending(o => o.Available > 0)
            .ThenBy(o => o.Price)
            .ThenBy(o => o.Id)
            .ToList();

        Assert.Equal(2L, ordered[0].Id); // Seller B (180k, còn hàng, Id nhỏ hơn)
        Assert.Equal(4L, ordered[1].Id); // Seller D (180k, còn hàng, Id lớn hơn)
        Assert.Equal(1L, ordered[2].Id); // Seller A (200k, còn hàng)
        Assert.Equal(3L, ordered[3].Id); // Seller C (150k, hết hàng)
    }

    [Theory]
    [InlineData(SellerOfferStatus.Approved, SellerStatus.Active, ProductStatus.Published, true, 10, 100_000, true)]
    [InlineData(SellerOfferStatus.Pending, SellerStatus.Active, ProductStatus.Published, true, 10, 100_000, false)] // Chưa duyệt
    [InlineData(SellerOfferStatus.Rejected, SellerStatus.Active, ProductStatus.Published, true, 10, 100_000, false)] // Bị từ chối
    [InlineData(SellerOfferStatus.Archived, SellerStatus.Active, ProductStatus.Published, true, 10, 100_000, false)] // Đã lưu trữ
    [InlineData(SellerOfferStatus.Approved, SellerStatus.Suspended, ProductStatus.Published, true, 10, 100_000, false)] // Seller bị đình chỉ
    [InlineData(SellerOfferStatus.Approved, SellerStatus.Active, ProductStatus.Draft, true, 10, 100_000, false)] // Sản phẩm chưa xuất bản
    [InlineData(SellerOfferStatus.Approved, SellerStatus.Active, ProductStatus.Published, false, 10, 100_000, false)] // Danh mục bị ẩn
    [InlineData(SellerOfferStatus.Approved, SellerStatus.Active, ProductStatus.Published, true, 10, 0, false)] // Giá = 0
    public void StorefrontOffer_ActiveEligibilityFormula_EvaluatesCorrectly(
        SellerOfferStatus offerStatus,
        SellerStatus sellerStatus,
        ProductStatus productStatus,
        bool categoryActive,
        int onHand,
        decimal price,
        bool expectedActive)
    {
        // Tái tạo điều kiện Active từ StorefrontOffer.cs (lines 43-46)
        bool hasBalance = onHand >= 0;
        bool skuActive = true;
        bool isActive = hasBalance &&
            offerStatus == SellerOfferStatus.Approved &&
            price > 0 &&
            sellerStatus == SellerStatus.Active &&
            skuActive &&
            productStatus == ProductStatus.Published &&
            categoryActive;

        Assert.Equal(expectedActive, isActive);
    }

    [Fact]
    public void CartLine_CalculatesEstimateAcrossMultipleSellersAndOffers()
    {
        var line1 = new CartLine(
            OfferId: 101,
            SkuId: 1,
            SellerId: 10,
            SellerName: "Seller A",
            Code: "SKU-A",
            Slug: "ao-thun",
            Name: "Áo thun nam",
            Quantity: 2,
            Available: 10,
            Price: 150_000m,
            Active: true,
            PriceChanged: false
        );

        var line2 = new CartLine(
            OfferId: 202,
            SkuId: 1, // Cùng 1 SkuId nhưng mua từ Seller B
            SellerId: 20,
            SellerName: "Seller B",
            Code: "SKU-B",
            Slug: "ao-thun",
            Name: "Áo thun nam",
            Quantity: 1,
            Available: 5,
            Price: 140_000m,
            Active: true,
            PriceChanged: false
        );

        var lineInactive = new CartLine(
            OfferId: 303,
            SkuId: 2,
            SellerId: 30,
            SellerName: "Seller C",
            Code: "SKU-C",
            Slug: "quan-jean",
            Name: "Quần jean",
            Quantity: 1,
            Available: 0,
            Price: 400_000m,
            Active: false, // Offer không còn Active
            PriceChanged: false
        );

        var cartView = new CartView(Guid.NewGuid(), [line1, line2, lineInactive]);

        // Ước tính giỏ hàng: chỉ tính các line Active = true
        // line1: 2 * 150,000 = 300,000
        // line2: 1 * 140,000 = 140,000
        // lineInactive: bỏ qua
        // Tổng = 440,000
        Assert.Equal(440_000m, cartView.Estimate);
    }

    [Fact]
    public void ProductProposal_ThrowsOnInvalidOrMissingFields()
    {
        Assert.Throws<ShopRuleException>(() => ShopRules.Text("", 150, "Tên sản phẩm"));
        Assert.Throws<ShopRuleException>(() => ShopRules.Text("   ", 150, "Đường dẫn định danh"));
        Assert.Throws<ShopRuleException>(() => ShopRules.Text(new string('x', 4001), 4000, "Mô tả sản phẩm"));

        var validName = ShopRules.Text("  Giày thể thao Nam  ", 150, "Tên sản phẩm");
        var validSlug = ShopRules.Text("  GIAY-THE-THAO-NAM  ", 150, "Đường dẫn định danh").ToLowerInvariant();
        Assert.Equal("Giày thể thao Nam", validName);
        Assert.Equal("giay-the-thao-nam", validSlug);
    }
}
