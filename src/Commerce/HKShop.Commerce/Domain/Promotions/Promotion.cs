namespace HKShop.Domain.Promotions;

public sealed class Promotion
{
    public long Id { get; set; }
    public string Code { get; set; } = "";
    public bool Active { get; set; }
    public bool IsPercentage { get; set; }
    public decimal Value { get; set; }
    public decimal MinimumVnd { get; set; }
    public int UsageCap { get; set; }
    public DateTimeOffset StartsAt { get; set; }
    public DateTimeOffset EndsAt { get; set; }

    public decimal Discount(decimal subtotal, DateTimeOffset now)
    {
        if (!Active || now < StartsAt || now >= EndsAt || subtotal < MinimumVnd)
            throw new ShopRuleException("The discount code is not eligible.");
        return Math.Min(subtotal, IsPercentage ? decimal.Floor(subtotal * Value / 100) : Value);
    }
}

public sealed class PromotionClaim
{
    public long Id { get; set; }
    public long PromotionId { get; set; }
    public Guid OrderId { get; set; }
    public string CustomerId { get; set; } = "";
    public ClaimStatus Status { get; set; }
}
