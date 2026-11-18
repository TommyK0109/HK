using HKShop.Infrastructure.Persistence.Configurations;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;

namespace HKShop.Infrastructure.Persistence;

public sealed class ShopUser : IdentityUser
{
    [System.ComponentModel.DataAnnotations.MaxLength(100)]
    public string DisplayName { get; set; } = "";
    [System.ComponentModel.DataAnnotations.MaxLength(20)]
    public string? ContactPhone { get; set; }
    public bool Disabled { get; set; }
}

public sealed class UserProfileImage
{
    public string UserId { get; set; } = "";
    public byte[] Data { get; set; } = [];
    public string ContentType { get; set; } = "";
}

public sealed class ShopDbContext(DbContextOptions<ShopDbContext> options) : IdentityDbContext<ShopUser>(options)
{
    protected override void OnModelCreating(ModelBuilder model)
    {
        base.OnModelCreating(model);
        model.Entity<ShopUser>().HasIndex(x => x.NormalizedEmail).HasDatabaseName("EmailIndex").IsUnique()
            .HasFilter("[NormalizedEmail] IS NOT NULL");
        model.Entity<UserProfileImage>(b =>
        {
            b.ToTable("UserProfileImages");
            b.HasKey(x => x.UserId);
            b.Property(x => x.ContentType).HasMaxLength(20);
            b.HasOne<ShopUser>().WithOne().HasForeignKey<UserProfileImage>(x => x.UserId);
        });

        CommerceModelConfiguration.Configure(model);
        ShippingModelConfiguration.Configure(model);
    }
}
