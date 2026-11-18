using HKShop.Domain.Orders;
using HKShop.Domain.Sellers;
using Microsoft.EntityFrameworkCore;

namespace HKShop.Infrastructure.Persistence.Configurations;

public static class ShippingModelConfiguration
{
    public static void Configure(ModelBuilder model)
    {
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
            b.HasOne<SellerOrder>().WithOne().HasForeignKey<TransportParcel>(x => x.SellerOrderId)
                .OnDelete(DeleteBehavior.Restrict);
            b.HasOne<SellerLocation>().WithMany().HasForeignKey(x => x.LocationId).OnDelete(DeleteBehavior.Restrict);
            b.HasOne<TransportShipper>().WithMany().HasForeignKey(x => x.ShipperId).OnDelete(DeleteBehavior.Restrict);
        });
        model.Entity<TransportShipper>(b =>
        {
            b.ToTable("TransportShippers");
            b.HasKey(x => x.UserId);
            b.Property(x => x.Email).HasMaxLength(256);
            b.HasIndex(x => new { x.Active, x.OnDuty, x.LocatedAt });
            b.HasOne<ShopUser>().WithOne().HasForeignKey<TransportShipper>(x => x.UserId).OnDelete(DeleteBehavior.Restrict);
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
            b.HasOne<TransportParcel>().WithMany().HasForeignKey(x => x.SellerOrderId).OnDelete(DeleteBehavior.Restrict);
            b.HasOne<TransportShipper>().WithMany().HasForeignKey(x => x.ShipperId).OnDelete(DeleteBehavior.Restrict);
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
            b.HasOne<TransportParcel>().WithMany().HasForeignKey(x => x.SellerOrderId).OnDelete(DeleteBehavior.Restrict);
            b.HasIndex(x => x.ReceivedAt);
        });
    }
}
