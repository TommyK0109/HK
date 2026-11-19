using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Identity;

namespace HKShop.Identity.Api.Domain;

public sealed class ShopUser : IdentityUser
{
    [MaxLength(100)]
    public string DisplayName { get; set; } = "";
    [MaxLength(20)]
    public string? ContactPhone { get; set; }
    public bool Disabled { get; set; }
}

public sealed class UserProfileImage
{
    public string UserId { get; set; } = "";
    public byte[] Data { get; set; } = [];
    public string ContentType { get; set; } = "";
}

public sealed class CustomerAddress
{
    public long Id { get; set; }
    public string CustomerId { get; set; } = "";
    public string Recipient { get; set; } = "";
    public string Phone { get; set; } = "";
    public string AddressText { get; set; } = "";
    public double? Latitude { get; set; }
    public double? Longitude { get; set; }
}

public sealed class RefreshToken
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string UserId { get; set; } = "";
    public string TokenHash { get; set; } = "";
    public string SecurityStamp { get; set; } = "";
    public bool MfaVerified { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
    public DateTimeOffset? RevokedAt { get; set; }
    public string? ReplacedByTokenHash { get; set; }
}
