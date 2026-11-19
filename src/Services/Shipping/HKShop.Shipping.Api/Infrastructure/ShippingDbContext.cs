using HKShop.Messaging;
using HKShop.Shipping.Api.Domain;
using Microsoft.EntityFrameworkCore;

namespace HKShop.Shipping.Api.Infrastructure;

public sealed class ShippingDbContext(DbContextOptions<ShippingDbContext> options) : DbContext(options)
{
    public DbSet<TransportParcel> TransportParcels => Set<TransportParcel>();
    public DbSet<TransportShipper> TransportShippers => Set<TransportShipper>();
    public DbSet<TransportAssignment> TransportAssignments => Set<TransportAssignment>();
    public DbSet<TransportBankReceipt> TransportBankReceipts => Set<TransportBankReceipt>();
    public DbSet<ShippingOrderSnapshot> OrderSnapshots => Set<ShippingOrderSnapshot>();
    public DbSet<ShippingSellerLocation> SellerLocations => Set<ShippingSellerLocation>();
    public DbSet<ShippingSellerMembership> SellerMemberships => Set<ShippingSellerMembership>();

    protected override void OnModelCreating(ModelBuilder model)
    {
        MessagingModelConfiguration.Configure(model);

        model.Entity<TransportParcel>(b =>
        {
            b.ToTable("TransportParcels");
            b.HasKey(x => x.SellerOrderId);
            b.Property(x => x.PickupAddress).HasMaxLength(300);
            b.Property(x => x.DeliveryCode).HasMaxLength(6);
            b.Property(x => x.ShipperId).HasMaxLength(450);
            b.Property(x => x.Issue).HasMaxLength(250);
            b.Property(x => x.ShipperPayoutReference).HasMaxLength(100);
            b.Property(x => x.ShipperFeeVnd).HasPrecision(18, 0);
            b.Property(x => x.CashCollectedVnd).HasPrecision(18, 0);
            b.HasIndex(x => new { x.Stage, x.PickupDueAt });
            b.HasIndex(x => new { x.Stage, x.NextDispatchAt });
            b.HasIndex(x => new { x.Stage, x.AcknowledgeBy });
            b.HasIndex(x => x.ShipperPayoutReference).IsUnique().HasFilter("[ShipperPayoutReference] IS NOT NULL");
        });

        model.Entity<TransportShipper>(b =>
        {
            b.ToTable("TransportShippers");
            b.HasKey(x => x.UserId);
            b.Property(x => x.Email).HasMaxLength(256);
            b.HasIndex(x => new { x.Active, x.OnDuty, x.LocatedAt });
        });

        model.Entity<TransportAssignment>(b =>
        {
            b.ToTable("TransportAssignments");
            b.Property(x => x.ShipperId).HasMaxLength(450);
            b.Property(x => x.Outcome).HasMaxLength(50);
            b.HasIndex(x => x.ShipperId).IsUnique().HasFilter("[EndedAt] IS NULL")
                .HasDatabaseName("IX_TransportAssignments_ActiveShipper");
            b.HasIndex(x => x.SellerOrderId).IsUnique().HasFilter("[EndedAt] IS NULL")
                .HasDatabaseName("IX_TransportAssignments_ActiveParcel");
            b.HasIndex(x => new { x.SellerOrderId, x.ShipperId });
        });

        model.Entity<TransportBankReceipt>(b =>
        {
            b.ToTable("TransportBankReceipts");
            b.HasKey(x => x.TransactionId);
            b.Property(x => x.TransactionId).HasMaxLength(100);
            b.Property(x => x.Reference).HasMaxLength(100);
            b.Property(x => x.AmountVnd).HasPrecision(18, 0);
            b.Property(x => x.Issue).HasMaxLength(250);
            b.Property(x => x.Resolution).HasMaxLength(250);
            b.HasIndex(x => x.ReceivedAt);
        });

        model.Entity<ShippingOrderSnapshot>(b =>
        {
            b.ToTable("ShippingOrderSnapshots");
            b.HasKey(x => x.SellerOrderId);
            b.Property(x => x.SellerOrderNumber).HasMaxLength(50);
            b.Property(x => x.TotalVnd).HasPrecision(18, 0);
            b.Property(x => x.ShippingVnd).HasPrecision(18, 0);
            b.Property(x => x.Recipient).HasMaxLength(100);
            b.Property(x => x.Phone).HasMaxLength(20);
            b.Property(x => x.ShippingAddress).HasMaxLength(300);
            b.HasIndex(x => x.OrderId);
        });

        model.Entity<ShippingSellerLocation>(b =>
        {
            b.ToTable("ShippingSellerLocations");
            b.HasKey(x => x.LocationId);
            b.Property(x => x.LocationId).ValueGeneratedNever();
            b.Property(x => x.Name).HasMaxLength(100);
            b.Property(x => x.PickupAddress).HasMaxLength(300);
            b.HasIndex(x => new { x.SellerId, x.Active });
        });

        model.Entity<ShippingSellerMembership>(b =>
        {
            b.ToTable("ShippingSellerMemberships");
            b.HasKey(x => new { x.SellerId, x.UserId });
        });
    }
}
