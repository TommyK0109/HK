namespace HKShop.Domain.Cart;

public sealed class ShoppingCart
{
    public long Id { get; set; }
    public string? CustomerId { get; set; }
    public string? GuestTokenHash { get; set; }
    public Guid Revision { get; set; } = Guid.NewGuid();
    public DateTimeOffset UpdatedAt { get; set; }
    public List<CartItem> Items { get; set; } = [];
}

public sealed class CartItem
{
    public long CartId { get; set; }
    public long OfferId { get; set; }
    public long SkuId { get; set; }
    public int Quantity { get; set; }
    public decimal? SeenUnitPriceVnd { get; set; }
}
