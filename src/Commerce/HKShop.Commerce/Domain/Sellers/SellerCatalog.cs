namespace HKShop.Domain.Sellers;

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
