using HKShop.Contracts.Ports;
using HKShop.Domain;
using HKShop.Domain.Catalog;
using HKShop.Domain.Orders;
using HKShop.Domain.Sellers;

namespace HKShop.Catalog.Api.Domain;

public sealed class Category
{
    public long Id { get; set; }
    public string Name { get; set; } = "";
    public bool Active { get; set; } = true;
}

public sealed class Product
{
    public int Id { get; set; }
    public long SellerId { get; set; } = Seller.FirstPartyId;
    public long? CategoryId { get; set; }
    public string Name { get; set; } = "";
    public string Slug { get; set; } = "";
    public string Description { get; set; } = "";
    public decimal? LegacyPrice { get; set; }
    public ProductStatus Status { get; set; }
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
    public long Id { get; set; }
    public int ProductId { get; set; }
    public string CustomerId { get; set; } = "";
    public string Content { get; set; } = "";
    public int Rating { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? UpdatedAt { get; set; }
}

public sealed class AboListing
{
    public string DomainName { get; set; } = "";
    public string ItemId { get; set; } = "";
    public int ProductId { get; set; }
    public string DefaultLanguageTag { get; set; } = "";
    public string MetadataJson { get; set; } = "";
}

public sealed class ProductTranslation
{
    public int ProductId { get; set; }
    public string LanguageTag { get; set; } = "";
    public string? Name { get; set; }
    public string? Description { get; set; }
}

public sealed class AboImage
{
    public string ImageId { get; set; } = "";
    public string SourcePath { get; set; } = "";
    public string FileName { get; set; } = "";
    public int Width { get; set; }
    public int Height { get; set; }
}

public sealed class StockBalance
{
    public long SkuId { get; set; }
    public int OnHand { get; set; }
    public int Reserved { get; set; }
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

public sealed class Seller
{
    public const long FirstPartyId = 1;
    public long Id { get; set; }
    public string Slug { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public SellerStatus Status { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

public sealed class SellerMembership
{
    public long SellerId { get; set; }
    public string UserId { get; set; } = "";
    public string Email { get; set; } = "";
    public SellerRole Role { get; set; }
    public SellerMembershipStatus Status { get; set; }
}

public sealed class SellerInvitation
{
    public long Id { get; set; }
    public long SellerId { get; set; }
    public string Email { get; set; } = "";
    public string NormalizedEmail { get; set; } = "";
    public SellerRole Role { get; set; }
    public string TokenHash { get; set; } = "";
    public DateTimeOffset ExpiresAt { get; set; }
    public DateTimeOffset? AcceptedAt { get; set; }
    public DateTimeOffset? RevokedAt { get; set; }
}

public sealed class SellerLocation
{
    public const long FirstPartyId = 1;
    public long Id { get; set; }
    public long SellerId { get; set; }
    public string Name { get; set; } = "";
    public string? PickupAddress { get; set; }
    public double? Latitude { get; set; }
    public double? Longitude { get; set; }
    public bool Active { get; set; } = true;
}

public sealed class SellerOffer
{
    public long Id { get; set; }
    public long SellerId { get; set; }
    public long SkuId { get; set; }
    public string SellerSkuCode { get; set; } = "";
    public decimal? PriceVnd { get; set; }
    public SellerOfferStatus Status { get; set; }
}

public sealed class InventoryBalance
{
    public long Id { get; set; }
    public long SellerId { get; set; }
    public long OfferId { get; set; }
    public long LocationId { get; set; }
    public int OnHand { get; set; }
    public int Reserved { get; set; }
    public int Available => OnHand - Reserved;

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

    public void Restock(int quantity)
    {
        if (quantity <= 0 || (long)OnHand + quantity > int.MaxValue)
            throw new ShopRuleException("Invalid quantity to restock.");
        OnHand += quantity;
    }
}

public sealed class OfferStockMovement
{
    public long Id { get; set; }
    public long InventoryBalanceId { get; set; }
    public int OnHandDelta { get; set; }
    public int ReservedDelta { get; set; }
    public string Reason { get; set; } = "";
    public string? ActorId { get; set; }
    public DateTimeOffset OccurredAt { get; set; }
}

public sealed class ProductProposal
{
    public long Id { get; set; }
    public long SellerId { get; set; }
    public long CategoryId { get; set; }
    public string Name { get; set; } = "";
    public string Slug { get; set; } = "";
    public string Description { get; set; } = "";
    public ProductProposalStatus Status { get; set; }
    public int? ProductId { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

public sealed class StoreApplication
{
    public Guid Id { get; set; }
    public string UserId { get; set; } = "";
    public string Email { get; set; } = "";
    public string StoreName { get; set; } = "";
    public string ProductsJson { get; set; } = "";
    public StoreApplicationStatus Status { get; set; }
    public DateTimeOffset SubmittedAt { get; set; }
    public DateTimeOffset? DecidedAt { get; set; }
    public string? ReviewerId { get; set; }
    public string? DeclineReason { get; set; }
    public long? SellerId { get; set; }
    public DateTimeOffset? ReadAt { get; set; }
}

public sealed class StoreApplicationVerification
{
    public Guid ApplicationId { get; set; }
    public string NationalId { get; set; } = "";
    public string BusinessLicenseNumber { get; set; } = "";
}

public sealed class StoreVerificationDocument
{
    public Guid ApplicationId { get; set; }
    public StoreDocumentKind Kind { get; set; }
    public byte[] Data { get; set; } = [];
    public string ContentType { get; set; } = "";
}

public sealed class CatalogStockHold
{
    public Guid? SellerOrderId { get; set; }
    public long Id { get; set; }
    public Guid OrderId { get; set; }
    public long OfferId { get; set; }
    public long SkuId { get; set; }
    public long SellerId { get; set; }
    public long InventoryBalanceId { get; set; }
    public int Quantity { get; set; }
    public ReservationStatus Status { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

public sealed class VerifiedPurchaseProjection
{
    public long Id { get; set; }
    public string CustomerId { get; set; } = "";
    public int ProductId { get; set; }
    public Guid SellerOrderId { get; set; }
    public DateTimeOffset DeliveredAt { get; set; }
}
