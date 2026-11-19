namespace HKShop.Domain.Catalog;

public sealed class Category
{
    public long Id { get; set; }
    public string Name { get; set; } = "";
    public bool Active { get; set; } = true;
}

public sealed class Product
{
    public int Id { get; set; }
    public long SellerId { get; set; } = Sellers.Seller.FirstPartyId;
    public long? CategoryId { get; set; }
    public string Name { get; set; } = "";
    public string Slug { get; set; } = "";
    public string Description { get; set; } = "";
    public decimal? LegacyPrice { get; set; }
    public ProductStatus Status { get; private set; }
    public List<Sku> Skus { get; set; } = [];
    public List<ProductImage> Images { get; set; } = [];

    public void Publish(bool categoryActive)
    {
        if (!categoryActive || Images.Count == 0 || !Skus.Any(sku => sku.Active))
            throw new ShopRuleException("An active category, an image, and at least one active variant are required to publish a product.");
        Status = ProductStatus.Published;
    }

    public void Archive() => Status = ProductStatus.Archived;
}

public sealed class Sku
{
    public long Id { get; set; }
    public int ProductId { get; set; }
    public string Code { get; set; } = "";
    public string Size { get; set; } = "";
    public string Color { get; set; } = "";
    public decimal? PriceVnd { get; set; }
    public bool Active { get; set; }
    public StockBalance Stock { get; set; } = new();
}

public sealed class ProductImage
{
    public long Id { get; set; }
    public int ProductId { get; set; }
    public string FileName { get; set; } = "";
    public string Alt { get; set; } = "";
}

public sealed class ProductReview
{
    public const int MaxContentLength = 2000;

    public long Id { get; private set; }
    public int ProductId { get; private set; }
    public string CustomerId { get; private set; } = "";
    public string Content { get; private set; } = "";
    public int Rating { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset? UpdatedAt { get; private set; }

    private ProductReview() { }

    public static ProductReview Submit(int productId, string customerId, string? content, int rating, DateTimeOffset createdAt)
    {
        if (productId <= 0 || string.IsNullOrWhiteSpace(customerId))
            throw new ShopRuleException("A product and an account are required to submit a review.");

        return new ProductReview
        {
            ProductId = productId, CustomerId = customerId, Content = Validate(content, rating), Rating = rating, CreatedAt = createdAt
        };
    }

    public void Edit(string? content, int rating, DateTimeOffset updatedAt)
    {
        Content = Validate(content, rating);
        Rating = rating;
        UpdatedAt = updatedAt;
    }

    private static string Validate(string? content, int rating)
    {
        if (rating is < 1 or > 5)
            throw new ShopRuleException("The rating must be between 1 and 5 stars.");

        var text = content?.Trim();
        if (string.IsNullOrEmpty(text) || text.Length > MaxContentLength)
            throw new ShopRuleException($"The review must be between 1 and {MaxContentLength} characters.");

        return text;
    }
}

public sealed class StockBalance
{
    public long SkuId { get; set; }
    public int OnHand { get; private set; }
    public int Reserved { get; private set; }
    public int LowThreshold { get; set; } = 5;
    public int Available => OnHand - Reserved;

    public void Adjust(int delta)
    {
        if ((long)OnHand + delta < Reserved || (long)OnHand + delta > int.MaxValue)
            throw new ShopRuleException("Invalid quantity to adjust.");
        OnHand += delta;
    }

    public void Reserve(int quantity)
    {
        if (quantity <= 0 || quantity > Available)
            throw new ShopRuleException("Products are not available.");
        Reserved += quantity;
    }

    public void Release(int quantity)
    {
        if (quantity <= 0 || quantity > Reserved)
            throw new ShopRuleException("Invalid quantity to release.");
        Reserved -= quantity;
    }

    public void Dispatch(int quantity)
    {
        Release(quantity);
        OnHand -= quantity;
    }
}

public sealed class StockMovement
{
    public long Id { get; set; }
    public long SkuId { get; set; }
    public Guid? OrderId { get; set; }
    public int OnHandDelta { get; set; }
    public int ReservedDelta { get; set; }
    public string Reason { get; set; } = "";
    public string? ActorId { get; set; }
    public DateTimeOffset OccurredAt { get; set; }
}
