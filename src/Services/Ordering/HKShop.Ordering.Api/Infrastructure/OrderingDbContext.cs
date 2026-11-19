using HKShop.Messaging;
using HKShop.Ordering.Api.Domain;
using Microsoft.EntityFrameworkCore;

namespace HKShop.Ordering.Api.Infrastructure;

public sealed class OrderingDbContext(DbContextOptions<OrderingDbContext> options) : DbContext(options)
{
    public DbSet<ShoppingCart> ShoppingCarts => Set<ShoppingCart>();
    public DbSet<CartItem> CartItems => Set<CartItem>();
    public DbSet<Promotion> Promotions => Set<Promotion>();
    public DbSet<PromotionClaim> PromotionClaims => Set<PromotionClaim>();
    public DbSet<Order> Orders => Set<Order>();
    public DbSet<SellerOrder> SellerOrders => Set<SellerOrder>();
    public DbSet<OrderItem> OrderItems => Set<OrderItem>();
    public DbSet<OrderEvent> OrderEvents => Set<OrderEvent>();
    public DbSet<Shipment> Shipments => Set<Shipment>();
    public DbSet<ReturnRequest> ReturnRequests => Set<ReturnRequest>();
    public DbSet<ReturnItem> ReturnItems => Set<ReturnItem>();
    public DbSet<CheckoutRequest> CheckoutRequests => Set<CheckoutRequest>();
    public DbSet<OrderingOfferProjection> OfferProjections => Set<OrderingOfferProjection>();
    public DbSet<OrderingSellerMembershipProjection> SellerMemberships => Set<OrderingSellerMembershipProjection>();

    protected override void OnModelCreating(ModelBuilder model)
    {
        MessagingModelConfiguration.Configure(model);

        model.Entity<ShoppingCart>(b =>
        {
            b.ToTable("ShoppingCarts", t => t.HasCheckConstraint("CK_ShoppingCarts_Owner",
                "([CustomerId] IS NULL AND [GuestTokenHash] IS NOT NULL) OR ([CustomerId] IS NOT NULL AND [GuestTokenHash] IS NULL)"));
            b.Property(x => x.CustomerId).HasMaxLength(450);
            b.Property(x => x.GuestTokenHash).HasMaxLength(64);
            b.HasIndex(x => x.GuestTokenHash).IsUnique().HasFilter("[GuestTokenHash] IS NOT NULL");
            b.HasIndex(x => x.CustomerId).IsUnique().HasFilter("[CustomerId] IS NOT NULL");
            b.HasIndex(x => x.UpdatedAt);
            b.HasMany(x => x.Items).WithOne().HasForeignKey(x => x.CartId);
            b.Property<byte[]>("RowVersion").IsRowVersion();
        });

        model.Entity<CartItem>(b =>
        {
            b.ToTable("CartItems", t => t.HasCheckConstraint("CK_CartItems_Quantity", "[Quantity] BETWEEN 1 AND 999"));
            b.HasKey(x => new { x.CartId, x.OfferId });
            b.Property(x => x.SeenUnitPriceVnd).HasPrecision(18, 0);
        });

        model.Entity<Promotion>(b =>
        {
            b.ToTable("Promotions");
            b.Property(x => x.Code).HasMaxLength(40);
            b.Property(x => x.MinimumVnd).HasPrecision(18, 0);
            b.Property(x => x.Value).HasPrecision(20, 2);
            b.HasIndex(x => x.Code).IsUnique();
            b.Property<byte[]>("RowVersion").IsRowVersion();
        });

        model.Entity<PromotionClaim>(b =>
        {
            b.ToTable("PromotionClaims");
            b.Property(x => x.CustomerId).HasMaxLength(450);
            b.HasIndex(x => x.OrderId).IsUnique();
            b.HasIndex(x => new { x.PromotionId, x.CustomerId }).IsUnique().HasFilter("[Status] <> 2");
            b.HasOne<Promotion>().WithMany().HasForeignKey(x => x.PromotionId).OnDelete(DeleteBehavior.Restrict);
            b.HasOne<Order>().WithMany().HasForeignKey(x => x.OrderId).OnDelete(DeleteBehavior.Restrict);
        });

        model.Entity<Order>(b =>
        {
            b.ToTable("Orders");
            b.Property(x => x.Number).HasMaxLength(34);
            b.Property(x => x.CustomerId).HasMaxLength(450);
            b.Property(x => x.CustomerEmail).HasMaxLength(256);
            b.Property(x => x.Recipient).HasMaxLength(100);
            b.Property(x => x.Phone).HasMaxLength(20);
            b.Property(x => x.ShippingAddress).HasMaxLength(300);
            b.Property(x => x.ShippingVnd).HasPrecision(18, 0);
            b.Property(x => x.TotalVnd).HasPrecision(18, 0);
            b.HasIndex(x => x.Number).IsUnique();
            b.HasIndex(x => new { x.CustomerId, x.SubmissionKey }).IsUnique();
            b.HasIndex(x => new { x.CustomerId, x.CreatedAt });
            b.HasIndex(x => new { x.Status, x.PaymentDeadline });
            b.HasIndex(x => x.CreatedAt);
            b.HasMany(x => x.SellerOrders).WithOne().HasForeignKey(x => x.OrderId).OnDelete(DeleteBehavior.Restrict);
            b.HasMany(x => x.Items).WithOne().HasForeignKey(x => x.OrderId).OnDelete(DeleteBehavior.Restrict);
            b.HasMany(x => x.Events).WithOne().HasForeignKey(x => x.OrderId).OnDelete(DeleteBehavior.Restrict);
            b.Property<byte[]>("RowVersion").IsRowVersion();
        });

        model.Entity<SellerOrder>(b =>
        {
            b.ToTable("SellerOrders", t => t.HasCheckConstraint("CK_SellerOrders_Amounts",
                "[SubtotalVnd] >= 0 AND [SellerDiscountVnd] >= 0 AND [PlatformDiscountVnd] >= 0 AND [ShippingVnd] >= 0 AND [TotalVnd] >= 0 AND [CommissionVnd] >= 0"));
            b.Property(x => x.Number).HasMaxLength(50);
            b.Property(x => x.SubtotalVnd).HasPrecision(18, 0);
            b.Property(x => x.SellerDiscountVnd).HasPrecision(18, 0);
            b.Property(x => x.PlatformDiscountVnd).HasPrecision(18, 0);
            b.Property(x => x.ShippingVnd).HasPrecision(18, 0);
            b.Property(x => x.TotalVnd).HasPrecision(18, 0);
            b.Property(x => x.CommissionVnd).HasPrecision(18, 0);
            b.HasAlternateKey(x => new { x.Id, x.OrderId });
            b.HasAlternateKey(x => new { x.Id, x.OrderId, x.SellerId });
            b.HasIndex(x => x.Number).IsUnique();
            b.HasIndex(x => new { x.OrderId, x.SellerId }).IsUnique();
            b.HasIndex(x => new { x.SellerId, x.Status, x.CreatedAt });
            b.HasOne(x => x.Shipment).WithOne().HasForeignKey<Shipment>(x => new { x.SellerOrderId, x.OrderId })
                .HasPrincipalKey<SellerOrder>(x => new { x.Id, x.OrderId }).OnDelete(DeleteBehavior.Restrict);
            b.Property<byte[]>("RowVersion").IsRowVersion();
        });

        model.Entity<OrderItem>(b =>
        {
            b.ToTable("OrderItems", t => t.HasCheckConstraint("CK_OrderItems_Amounts",
                "[CommissionBps] BETWEEN 0 AND 10000 AND [Quantity] > 0 AND [UnitPriceVnd] >= 0 AND [DiscountVnd] >= 0 AND [SellerDiscountVnd] >= 0 AND [PlatformDiscountVnd] >= 0 AND [CommissionVnd] >= 0"));
            b.Property(x => x.SkuCode).HasMaxLength(64);
            b.Property(x => x.SellerSkuCode).HasMaxLength(64);
            b.Property(x => x.Name).HasMaxLength(150);
            b.Property(x => x.ProductAndVariantSnapshot).HasMaxLength(300);
            b.Property(x => x.UnitPriceVnd).HasPrecision(18, 0);
            b.Property(x => x.DiscountVnd).HasPrecision(18, 0);
            b.Property(x => x.SellerDiscountVnd).HasPrecision(18, 0);
            b.Property(x => x.PlatformDiscountVnd).HasPrecision(18, 0);
            b.Property(x => x.CommissionVnd).HasPrecision(18, 0);
            b.HasAlternateKey(x => new { x.Id, x.SellerOrderId });
            b.HasIndex(x => new { x.SellerOrderId, x.OfferId });
            b.HasOne<SellerOrder>().WithMany().HasForeignKey(x => new { x.SellerOrderId, x.OrderId, x.SellerId })
                .HasPrincipalKey(x => new { x.Id, x.OrderId, x.SellerId }).OnDelete(DeleteBehavior.Restrict);
        });

        model.Entity<OrderEvent>(b =>
        {
            b.ToTable("OrderEvents");
            b.Property(x => x.Note).HasMaxLength(250);
        });

        model.Entity<Shipment>(b =>
        {
            b.ToTable("Shipments");
            b.HasKey(x => x.SellerOrderId);
            b.HasIndex(x => x.OrderId);
            b.Property(x => x.Carrier).HasMaxLength(100);
            b.Property(x => x.TrackingNumber).HasMaxLength(100);
        });

        model.Entity<ReturnRequest>(b =>
        {
            b.ToTable("ReturnRequests");
            b.Property(x => x.Reason).HasMaxLength(250);
            b.Property(x => x.StaffNote).HasMaxLength(250);
            b.HasAlternateKey(x => new { x.Id, x.SellerOrderId });
            b.HasIndex(x => x.OrderId);
            b.HasIndex(x => new { x.SellerOrderId, x.Status });
            b.HasOne<SellerOrder>().WithMany().HasForeignKey(x => new { x.SellerOrderId, x.OrderId })
                .HasPrincipalKey(x => new { x.Id, x.OrderId }).OnDelete(DeleteBehavior.Restrict);
            b.HasMany(x => x.Items).WithOne().HasForeignKey(x => new { x.ReturnId, x.SellerOrderId })
                .HasPrincipalKey(x => new { x.Id, x.SellerOrderId });
            b.Property<byte[]>("RowVersion").IsRowVersion();
        });

        model.Entity<ReturnItem>(b =>
        {
            b.ToTable("ReturnItems", t => t.HasCheckConstraint("CK_ReturnItems_Quantity",
                "[Requested] > 0 AND [Accepted] BETWEEN 0 AND [Requested] AND [Restocked] BETWEEN 0 AND [Accepted]"));
            b.HasIndex(x => new { x.ReturnId, x.OrderItemId }).IsUnique();
            b.HasOne<OrderItem>().WithMany().HasForeignKey(x => new { x.OrderItemId, x.SellerOrderId })
                .HasPrincipalKey(x => new { x.Id, x.SellerOrderId }).OnDelete(DeleteBehavior.Restrict);
        });

        model.Entity<CheckoutRequest>(b =>
        {
            b.ToTable("CheckoutRequests");
            b.Property(r => r.Id).ValueGeneratedNever();
            b.Property(r => r.CustomerId).HasMaxLength(450);
            b.Property(r => r.Error).HasMaxLength(500);
            b.HasIndex(r => new { r.Status, r.PublishedAt, r.CreatedAt });
            b.HasIndex(r => new { r.Status, r.ExpiresAt });
        });

        model.Entity<OrderingOfferProjection>(b =>
        {
            b.ToTable("OrderingOfferProjections");
            b.HasKey(x => x.OfferId);
            b.Property(x => x.OfferId).ValueGeneratedNever();
            b.Property(x => x.SellerName).HasMaxLength(100);
            b.Property(x => x.SellerSkuCode).HasMaxLength(64);
            b.Property(x => x.Slug).HasMaxLength(150);
            b.Property(x => x.Name).HasMaxLength(150);
            b.Property(x => x.PriceVnd).HasPrecision(18, 0);
        });

        model.Entity<OrderingSellerMembershipProjection>(b =>
        {
            b.ToTable("OrderingSellerMemberships");
            b.HasKey(x => new { x.SellerId, x.UserId });
        });
    }
}
