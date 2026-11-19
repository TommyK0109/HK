using HKShop.Identity.Api.Domain;
using HKShop.Messaging;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;

namespace HKShop.Identity.Api.Infrastructure;

public sealed class IdentityServiceDbContext(DbContextOptions<IdentityServiceDbContext> options)
    : IdentityDbContext<ShopUser>(options)
{
    public DbSet<UserProfileImage> UserProfileImages => Set<UserProfileImage>();
    public DbSet<CustomerAddress> CustomerAddresses => Set<CustomerAddress>();
    public DbSet<RefreshToken> RefreshTokens => Set<RefreshToken>();

    protected override void OnModelCreating(ModelBuilder model)
    {
        base.OnModelCreating(model);
        MessagingModelConfiguration.Configure(model);

        model.Entity<ShopUser>().HasIndex(x => x.NormalizedEmail).HasDatabaseName("EmailIndex").IsUnique()
            .HasFilter("[NormalizedEmail] IS NOT NULL");

        model.Entity<UserProfileImage>(b =>
        {
            b.ToTable("UserProfileImages");
            b.HasKey(x => x.UserId);
            b.Property(x => x.ContentType).HasMaxLength(20);
            b.HasOne<ShopUser>().WithOne().HasForeignKey<UserProfileImage>(x => x.UserId);
        });

        model.Entity<CustomerAddress>(b =>
        {
            b.ToTable("CustomerAddresses");
            b.Property(x => x.CustomerId).HasMaxLength(450);
            b.Property(x => x.Recipient).HasMaxLength(100);
            b.Property(x => x.Phone).HasMaxLength(20);
            b.Property(x => x.AddressText).HasMaxLength(300);
            b.HasOne<ShopUser>().WithMany().HasForeignKey(x => x.CustomerId).OnDelete(DeleteBehavior.Restrict);
        });

        model.Entity<RefreshToken>(b =>
        {
            b.ToTable("RefreshTokens");
            b.Property(x => x.UserId).HasMaxLength(450);
            b.Property(x => x.TokenHash).HasMaxLength(64).IsUnicode(false);
            b.Property(x => x.SecurityStamp).HasMaxLength(128);
            b.Property(x => x.ReplacedByTokenHash).HasMaxLength(64).IsUnicode(false);
            b.HasIndex(x => x.TokenHash).IsUnique();
            b.HasIndex(x => new { x.UserId, x.RevokedAt });
            b.HasOne<ShopUser>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
        });
    }
}
