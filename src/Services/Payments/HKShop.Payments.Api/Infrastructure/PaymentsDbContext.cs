using HKShop.Domain.Sellers;
using HKShop.Messaging;
using HKShop.Payments.Api.Domain;
using Microsoft.EntityFrameworkCore;

namespace HKShop.Payments.Api.Infrastructure;

public sealed class PaymentsDbContext(DbContextOptions<PaymentsDbContext> options) : DbContext(options)
{
    public DbSet<Payment> Payments => Set<Payment>();
    public DbSet<PaymentAttempt> PaymentAttempts => Set<PaymentAttempt>();
    public DbSet<PaymentEvent> PaymentEvents => Set<PaymentEvent>();
    public DbSet<Refund> Refunds => Set<Refund>();
    public DbSet<RefundItem> RefundItems => Set<RefundItem>();
    public DbSet<LedgerAccount> LedgerAccounts => Set<LedgerAccount>();
    public DbSet<LedgerJournal> LedgerJournals => Set<LedgerJournal>();
    public DbSet<LedgerEntry> LedgerEntries => Set<LedgerEntry>();
    public DbSet<Payout> Payouts => Set<Payout>();
    public DbSet<SimulatedPayoutTransfer> SimulatedPayoutTransfers => Set<SimulatedPayoutTransfer>();
    public DbSet<PaymentOrderProjection> OrderProjections => Set<PaymentOrderProjection>();
    public DbSet<PaymentSellerOrderProjection> SellerOrderProjections => Set<PaymentSellerOrderProjection>();
    public DbSet<PaymentSellerProjection> SellerProjections => Set<PaymentSellerProjection>();
    public DbSet<PaymentSellerMembershipProjection> SellerMemberships => Set<PaymentSellerMembershipProjection>();

    protected override void OnModelCreating(ModelBuilder model)
    {
        MessagingModelConfiguration.Configure(model);

        model.Entity<Payment>(b =>
        {
            b.ToTable("Payments");
            b.HasKey(x => x.OrderId);
            b.Property(x => x.AmountVnd).HasPrecision(18, 0);
            b.Property(x => x.ProviderTransaction).HasMaxLength(120);
            b.Property(x => x.CollectionReference).HasMaxLength(100);
            b.Property(x => x.Issue).HasMaxLength(250);
            b.HasIndex(x => x.ProviderTransaction).IsUnique().HasFilter("[ProviderTransaction] IS NOT NULL");
            b.HasIndex(x => x.NextQueryAt);
            b.HasIndex(x => x.PaidAt);
            b.Property<byte[]>("RowVersion").IsRowVersion();
        });

        model.Entity<PaymentAttempt>(b =>
        {
            b.ToTable("PaymentAttempts", t => t.HasCheckConstraint("CK_PaymentAttempts_Amounts",
                "[AttemptNumber] > 0 AND [AmountVnd] >= 0 AND [CapturedAmountVnd] >= 0"));
            b.Property(x => x.AmountVnd).HasPrecision(18, 0);
            b.Property(x => x.CapturedAmountVnd).HasPrecision(18, 0);
            b.Property(x => x.ProviderInvoice).HasMaxLength(50);
            b.Property(x => x.ProviderTransaction).HasMaxLength(120);
            b.Property(x => x.Issue).HasMaxLength(250);
            b.HasAlternateKey(x => new { x.Id, x.OrderId });
            b.HasIndex(x => new { x.OrderId, x.AttemptNumber }).IsUnique();
            b.HasIndex(x => x.OrderId).IsUnique().HasFilter("[AppliedToOrder] = 1 AND [SellerOrderId] IS NULL")
                .HasDatabaseName("IX_PaymentAttempts_OrderId_AppliedToOrder");
            b.HasIndex(x => x.SellerOrderId).IsUnique().HasFilter("[AppliedToOrder] = 1 AND [SellerOrderId] IS NOT NULL");
            b.HasIndex(x => x.ProviderInvoice).IsUnique().HasFilter("[ProviderInvoice] IS NOT NULL");
            b.HasIndex(x => x.ProviderTransaction).IsUnique().HasFilter("[ProviderTransaction] IS NOT NULL");
            b.HasIndex(x => new { x.Status, x.NextQueryAt });
            b.HasIndex(x => x.PaidAt);
            b.Property<byte[]>("RowVersion").IsRowVersion();
        });

        model.Entity<PaymentEvent>(b =>
        {
            b.ToTable("PaymentEvents");
            b.Property(x => x.EventType).HasMaxLength(60);
            b.Property(x => x.EventKey).HasMaxLength(150);
            b.HasIndex(x => x.EventKey).IsUnique();
            b.HasIndex(x => new { x.PaymentAttemptId, x.ReceivedAt });
            b.HasOne<PaymentAttempt>().WithMany().HasForeignKey(x => x.PaymentAttemptId).OnDelete(DeleteBehavior.Restrict);
        });

        model.Entity<Refund>(b =>
        {
            b.ToTable("Refunds", t => t.HasCheckConstraint("CK_Refunds_Amount",
                "[AmountVnd] > 0 AND [ShippingVnd] BETWEEN 0 AND [AmountVnd] AND ([ReturnId] IS NULL OR [SellerOrderId] IS NOT NULL)"));
            b.Property(x => x.AmountVnd).HasPrecision(18, 0);
            b.Property(x => x.ShippingVnd).HasPrecision(18, 0);
            b.Property(x => x.BusinessKey).HasMaxLength(120);
            b.Property(x => x.Reference).HasMaxLength(100);
            b.Property(x => x.ProviderReference).HasMaxLength(120);
            b.Property(x => x.Issue).HasMaxLength(250);
            b.HasIndex(x => x.BusinessKey).IsUnique();
            b.HasIndex(x => new { x.Id, x.SellerOrderId }).IsUnique().HasFilter(null);
            b.HasIndex(x => x.ReturnId).IsUnique().HasFilter("[ReturnId] IS NOT NULL");
            b.HasIndex(x => x.CompletedAt);
            b.HasIndex(x => new { x.Status, x.RequestedAt });
            b.HasIndex(x => new { x.Status, x.NextAttemptAt });
            b.HasMany(x => x.Items).WithOne().HasForeignKey(x => x.RefundId).OnDelete(DeleteBehavior.Restrict);
        });

        model.Entity<RefundItem>(b =>
        {
            b.ToTable("RefundItems", t => t.HasCheckConstraint("CK_RefundItems_Amounts",
                "[Quantity] > 0 AND [AmountVnd] >= 0 AND [CommissionReversalVnd] >= 0 AND [PlatformDiscountReversalVnd] >= 0"));
            b.HasKey(x => new { x.RefundId, x.OrderItemId });
            b.Property(x => x.AmountVnd).HasPrecision(18, 0);
            b.Property(x => x.CommissionReversalVnd).HasPrecision(18, 0);
            b.Property(x => x.PlatformDiscountReversalVnd).HasPrecision(18, 0);
        });

        model.Entity<LedgerAccount>(b =>
        {
            b.ToTable("LedgerAccounts", t => t.HasCheckConstraint("CK_LedgerAccounts_Scope",
                "([Type] BETWEEN 6 AND 8 AND [SellerId] IS NOT NULL) OR (([Type] BETWEEN 0 AND 5 OR [Type] BETWEEN 9 AND 11) AND [SellerId] IS NULL)"));
            b.HasIndex(x => x.Type).IsUnique().HasFilter("[SellerId] IS NULL");
            b.HasIndex(x => new { x.SellerId, x.Type }).IsUnique().HasFilter("[SellerId] IS NOT NULL");
        });

        model.Entity<LedgerJournal>(b =>
        {
            b.ToTable("LedgerJournals");
            b.Property(x => x.BusinessKey).HasMaxLength(120);
            b.HasIndex(x => x.BusinessKey).IsUnique();
            b.HasIndex(x => new { x.SellerOrderId, x.Type });
            b.HasMany(x => x.Entries).WithOne().HasForeignKey(x => x.JournalId).OnDelete(DeleteBehavior.Restrict);
        });

        model.Entity<LedgerEntry>(b =>
        {
            b.ToTable("LedgerEntries", t => t.HasCheckConstraint("CK_LedgerEntries_AmountAndSide", "[AmountVnd] > 0 AND [Side] IN (0, 1)"));
            b.Property(x => x.AmountVnd).HasPrecision(18, 0);
            b.HasIndex(x => new { x.AccountId, x.JournalId }).IsUnique();
            b.HasOne<LedgerAccount>().WithMany().HasForeignKey(x => x.AccountId).OnDelete(DeleteBehavior.Restrict);
        });

        model.Entity<Payout>(b =>
        {
            b.ToTable("Payouts", t => t.HasCheckConstraint("CK_Payouts_AmountAndStatus",
                "[AmountVnd] > 0 AND [Status] BETWEEN 0 AND 4 AND [Attempts] >= 0"));
            b.Property(x => x.AmountVnd).HasPrecision(18, 0);
            b.Property(x => x.RequestedBy).HasMaxLength(450);
            b.Property(x => x.ProviderReference).HasMaxLength(120);
            b.Property(x => x.Issue).HasMaxLength(250);
            b.Property(x => x.ReviewNote).HasMaxLength(250);
            b.HasIndex(x => new { x.SellerId, x.SubmissionKey }).IsUnique();
            b.HasIndex(x => new { x.SellerId, x.RequestedAt });
            b.HasIndex(x => new { x.Status, x.NextAttemptAt });
            b.HasIndex(x => x.ProviderReference).IsUnique().HasFilter("[ProviderReference] <> N''");
            b.Property<byte[]>("RowVersion").IsRowVersion();
        });

        model.Entity<SimulatedPayoutTransfer>(b =>
        {
            b.ToTable("SimulatedPayoutTransfers", t => t.HasCheckConstraint("CK_SimulatedPayoutTransfers_Amount", "[AmountVnd] > 0"));
            b.HasKey(x => x.PayoutId);
            b.Property(x => x.ProviderReference).HasMaxLength(120);
            b.Property(x => x.AmountVnd).HasPrecision(18, 0);
        });

        model.Entity<PaymentOrderProjection>(b =>
        {
            b.ToTable("PaymentOrderProjections");
            b.HasKey(x => x.OrderId);
            b.Property(x => x.OrderNumber).HasMaxLength(34);
            b.Property(x => x.CustomerId).HasMaxLength(450);
            b.Property(x => x.CustomerEmail).HasMaxLength(256);
            b.Property(x => x.TotalVnd).HasPrecision(18, 0);
            b.HasIndex(x => new { x.Status, x.PaymentDeadline });
        });

        model.Entity<PaymentSellerOrderProjection>(b =>
        {
            b.ToTable("PaymentSellerOrderProjections");
            b.HasKey(x => x.SellerOrderId);
            b.Property(x => x.SellerOrderNumber).HasMaxLength(50);
            b.Property(x => x.SubtotalVnd).HasPrecision(18, 0);
            b.Property(x => x.SellerDiscountVnd).HasPrecision(18, 0);
            b.Property(x => x.PlatformDiscountVnd).HasPrecision(18, 0);
            b.Property(x => x.ShippingVnd).HasPrecision(18, 0);
            b.Property(x => x.TotalVnd).HasPrecision(18, 0);
            b.Property(x => x.CommissionVnd).HasPrecision(18, 0);
            b.HasIndex(x => x.OrderId);
        });

        model.Entity<PaymentSellerProjection>(b =>
        {
            b.ToTable("PaymentSellerProjections");
            b.HasKey(x => x.SellerId);
            b.Property(x => x.SellerId).ValueGeneratedNever();
            b.Property(x => x.DisplayName).HasMaxLength(100);
            b.HasData(new PaymentSellerProjection { SellerId = 1, DisplayName = "HKShop", Status = SellerStatus.Active });
        });

        model.Entity<PaymentSellerMembershipProjection>(b =>
        {
            b.ToTable("PaymentSellerMemberships");
            b.HasKey(x => new { x.SellerId, x.UserId });
        });
    }
}
