using System.ComponentModel.DataAnnotations;
using System.Security.Cryptography;
using System.Text;
using HKShop.Application;
using HKShop.Application.Accounts;
using HKShop.Domain;
using HKShop.Domain.Catalog;
using HKShop.Domain.Orders;
using HKShop.Infrastructure.Payments;
using Xunit;

namespace HKShop.UnitTests;

public sealed class CommerceRulesTests
{
    [Theory]
    [InlineData("", false)]
    [InlineData("invalid-address", false)]
    [InlineData("customer@example.test", true)]
    public void RegistrationValidatesEmail(string email, bool expected)
    {
        var input = new RegisterInput { Email = email, Password = "demo-password", DisplayName = "Khách thử nghiệm" };
        Assert.Equal(expected, Validator.TryValidateObject(input, new ValidationContext(input), [], true));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(5)]
    public void ProductReviewTrimsContentAndAcceptsRatingBoundaries(int rating)
    {
        var submittedAt = DateTimeOffset.UtcNow;
        var content = new string('a', ProductReview.MaxContentLength);
        var review = ProductReview.Submit(42, "customer", $"  {content}  ", rating, submittedAt);
        Assert.Equal((42, "customer", content, rating, submittedAt),
            (review.ProductId, review.CustomerId, review.Content, review.Rating, review.CreatedAt));
    }

    [Theory]
    [InlineData("Good", 0)]
    [InlineData("Good", 6)]
    [InlineData("  ", 3)]
    [InlineData(null, 3)]
    public void ProductReviewRejectsInvalidRatingOrMissingContent(string? content, int rating) =>
        Assert.Throws<ShopRuleException>(() => ProductReview.Submit(42, "customer", content, rating, DateTimeOffset.UtcNow));

    [Fact]
    public void ProductReviewRejectsOverlongContent() =>
        Assert.Throws<ShopRuleException>(() => ProductReview.Submit(42, "customer",
            new string('a', ProductReview.MaxContentLength + 1), 3, DateTimeOffset.UtcNow));

    [Fact]
    public void ReviewEditsPreserveCreationTimeAndRejectInvalidChanges()
    {
        var createdAt = DateTimeOffset.UtcNow;
        var updatedAt = createdAt.AddDays(1);
        var review = ProductReview.Submit(42, "customer", "Original", 4, createdAt);

        review.Edit("  Updated  ", 5, updatedAt);
        Assert.Throws<ShopRuleException>(() => review.Edit("Invalid rating", 6, updatedAt.AddHours(1)));
        Assert.Throws<ShopRuleException>(() => review.Edit("  ", 1, updatedAt.AddHours(1)));

        Assert.Equal((42, "customer", "Updated", 5, createdAt, (DateTimeOffset?)updatedAt),
            (review.ProductId, review.CustomerId, review.Content, review.Rating, review.CreatedAt, review.UpdatedAt));
    }

    [Fact]
    public void DispatchConsumesExactlyTheReservedStock()
    {
        var stock = new StockBalance();
        stock.Adjust(3);
        stock.Reserve(2);
        Assert.Throws<ShopRuleException>(() => stock.Adjust(-2));
        stock.Dispatch(2);
        Assert.Equal(1, stock.OnHand);
        Assert.Equal(0, stock.Reserved);
        Assert.Equal(1, stock.Available);
    }

    [Fact]
    public void PackingBlocksCancellation()
    {
        var order = new Order { Status = OrderStatus.Packing };
        Assert.Throws<ShopRuleException>(() => order.Cancel(DateTimeOffset.UtcNow, ""));
        Assert.Equal(OrderStatus.Packing, order.Status);
    }

    [Fact]
    public void PartialRefundsSumToTheOriginalDiscountedLineAmount()
    {
        var item = new OrderItem { Quantity = 3, UnitPriceVnd = 10_000, DiscountVnd = 1_001 };
        Assert.Equal(28_999m, item.RefundFor(0, 1) + item.RefundFor(1, 1) + item.RefundFor(2, 1));
    }

    [Fact]
    public void SePayUsesOrderedFieldsAndAnIndependentNotificationSecret()
    {
        using var client = new HttpClient();
        var gateway = new SePayGateway(client,
            new SePaySettings { MerchantId = "demo", MerchantSecret = "checkout-key", IpnSecret = "ipn-key" },
            new ShopSettings { PublicOrigin = "https://shop.example.test" });
        var id = Guid.Parse("b06b8224-1d35-4ad9-80f5-e80189008dd1");
        var form = gateway.BuildCheckout("HKORDER", id, 45_000);
        var callback = $"https://shop.example.test/payments/sepay/return/{id:D}";
        var canonical = "order_amount=45000,merchant=demo,currency=VND,operation=PURCHASE," +
            $"order_description=HKShop HKORDER,order_invoice_number=HKORDER,success_url={callback},error_url={callback},cancel_url={callback}";
        var signature = Convert.ToBase64String(HMACSHA256.HashData(Encoding.UTF8.GetBytes("checkout-key"),
            Encoding.UTF8.GetBytes(canonical)));
        Assert.Equal("https://pay-sandbox.sepay.vn/v1/checkout/init", form.Action);
        Assert.Equal("signature", form.Fields[^1].Key);
        Assert.Equal(signature, form.Fields[^1].Value);
        Assert.True(gateway.Authenticate("ipn-key"));
        Assert.False(gateway.Authenticate("checkout-key"));
    }
}
