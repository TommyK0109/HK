namespace HKShop.Domain.Sellers;

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
