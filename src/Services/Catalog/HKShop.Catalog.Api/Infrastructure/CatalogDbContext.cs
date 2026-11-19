using HKShop.Catalog.Api.Domain;
using HKShop.Messaging;
using Microsoft.EntityFrameworkCore;

namespace HKShop.Catalog.Api.Infrastructure;

public sealed class CatalogDbContext(DbContextOptions<CatalogDbContext> options) : DbContext(options)
{
    public DbSet<Category> Categories => Set<Category>();
    public DbSet<Product> Products => Set<Product>();
    public DbSet<Sku> Skus => Set<Sku>();
    public DbSet<ProductImage> ProductImages => Set<ProductImage>();
    public DbSet<ProductReview> ProductReviews => Set<ProductReview>();
    public DbSet<AboListing> AboListings => Set<AboListing>();
    public DbSet<ProductTranslation> ProductTranslations => Set<ProductTranslation>();
    public DbSet<AboImage> AboImages => Set<AboImage>();
    public DbSet<StockBalance> StockBalances => Set<StockBalance>();
    public DbSet<StockMovement> StockMovements => Set<StockMovement>();
    public DbSet<Seller> Sellers => Set<Seller>();
    public DbSet<SellerMembership> SellerMemberships => Set<SellerMembership>();
    public DbSet<SellerInvitation> SellerInvitations => Set<SellerInvitation>();
    public DbSet<SellerLocation> SellerLocations => Set<SellerLocation>();
    public DbSet<SellerOffer> SellerOffers => Set<SellerOffer>();
    public DbSet<InventoryBalance> InventoryBalances => Set<InventoryBalance>();
    public DbSet<OfferStockMovement> OfferStockMovements => Set<OfferStockMovement>();
    public DbSet<ProductProposal> ProductProposals => Set<ProductProposal>();
    public DbSet<StoreApplication> StoreApplications => Set<StoreApplication>();
    public DbSet<StoreApplicationVerification> StoreApplicationVerifications => Set<StoreApplicationVerification>();
    public DbSet<StoreVerificationDocument> StoreVerificationDocuments => Set<StoreVerificationDocument>();
    public DbSet<CatalogStockHold> StockHolds => Set<CatalogStockHold>();
    public DbSet<VerifiedPurchaseProjection> VerifiedPurchases => Set<VerifiedPurchaseProjection>();

    protected override void OnModelCreating(ModelBuilder model)
    {
        MessagingModelConfiguration.Configure(model);

        model.Entity<Seller>(b =>
        {
            b.ToTable("Sellers", t => t.HasCheckConstraint("CK_Sellers_Status", "[Status] BETWEEN 0 AND 3"));
            b.Property(x => x.Slug).HasMaxLength(64).IsUnicode(false);
            b.Property(x => x.DisplayName).HasMaxLength(100);
            b.HasIndex(x => x.Slug).IsUnique();
            b.HasData(new Seller
            {
                Id = Seller.FirstPartyId,
                Slug = "hkshop",
                DisplayName = "HKShop",
                Status = HKShop.Domain.Sellers.SellerStatus.Active,
                CreatedAt = DateTimeOffset.UnixEpoch
            });
        });

        model.Entity<StoreApplication>(b =>
        {
            b.ToTable("StoreApplications");
            b.Property(x => x.StoreName).HasMaxLength(100);
            b.Property(x => x.Email).HasMaxLength(256);
            b.Property(x => x.DeclineReason).HasMaxLength(250);
            b.HasIndex(x => new { x.UserId, x.SubmittedAt });
            b.HasIndex(x => new { x.Status, x.SubmittedAt });
            b.HasIndex(x => x.SellerId).IsUnique().HasFilter("[SellerId] IS NOT NULL");
            b.HasOne<Seller>().WithMany().HasForeignKey(x => x.SellerId).OnDelete(DeleteBehavior.Restrict);
        });

        model.Entity<StoreApplicationVerification>(b =>
        {
            b.ToTable("StoreApplicationVerifications");
            b.HasKey(x => x.ApplicationId);
            b.HasOne<StoreApplication>().WithOne().HasForeignKey<StoreApplicationVerification>(x => x.ApplicationId);
        });

        model.Entity<StoreVerificationDocument>(b =>
        {
            b.ToTable("StoreVerificationDocuments");
            b.HasKey(x => new { x.ApplicationId, x.Kind });
            b.Property(x => x.ContentType).HasMaxLength(32);
            b.HasOne<StoreApplicationVerification>().WithMany().HasForeignKey(x => x.ApplicationId);
        });

        model.Entity<SellerMembership>(b =>
        {
            b.ToTable("SellerMemberships", t =>
            {
                t.HasCheckConstraint("CK_SellerMemberships_Role", "[Role] BETWEEN 0 AND 3");
                t.HasCheckConstraint("CK_SellerMemberships_Status", "[Status] BETWEEN 0 AND 1");
            });
            b.HasKey(x => new { x.SellerId, x.UserId }).IsClustered(false);
            b.Property(x => x.Email).HasMaxLength(256);
            b.HasOne<Seller>().WithMany().HasForeignKey(x => x.SellerId).OnDelete(DeleteBehavior.Restrict);
        });

        model.Entity<SellerInvitation>(b =>
        {
            b.ToTable("SellerInvitations", t => t.HasCheckConstraint("CK_SellerInvitations_Role", "[Role] BETWEEN 1 AND 3"));
            b.Property(x => x.Email).HasMaxLength(256);
            b.Property(x => x.NormalizedEmail).HasMaxLength(256);
            b.Property(x => x.TokenHash).HasMaxLength(64).IsUnicode(false);
            b.HasIndex(x => x.TokenHash).IsUnique();
            b.HasIndex(x => new { x.SellerId, x.NormalizedEmail });
            b.HasOne<Seller>().WithMany().HasForeignKey(x => x.SellerId).OnDelete(DeleteBehavior.Restrict);
        });

        model.Entity<SellerLocation>(b =>
        {
            b.ToTable("SellerLocations");
            b.Property(x => x.Name).HasMaxLength(100);
            b.Property(x => x.PickupAddress).HasMaxLength(300);
            b.HasAlternateKey(x => new { x.Id, x.SellerId });
            b.HasIndex(x => x.SellerId).IsUnique().HasFilter("[Active] = 1");
            b.HasOne<Seller>().WithMany().HasForeignKey(x => x.SellerId).OnDelete(DeleteBehavior.Restrict);
            b.HasData(new SellerLocation
            {
                Id = SellerLocation.FirstPartyId,
                SellerId = Seller.FirstPartyId,
                Name = "Kho HKShop",
                Active = true
            });
        });

        model.Entity<SellerOffer>(b =>
        {
            b.ToTable("SellerOffers", t =>
            {
                t.HasCheckConstraint("CK_SellerOffers_Status", "[Status] BETWEEN 0 AND 3");
                t.HasCheckConstraint("CK_SellerOffers_Price", "[PriceVnd] >= 0");
            });
            b.Property(x => x.SellerSkuCode).HasMaxLength(64);
            b.Property(x => x.PriceVnd).HasPrecision(18, 0);
            b.HasAlternateKey(x => new { x.Id, x.SellerId });
            b.HasAlternateKey(x => new { x.Id, x.SkuId });
            b.HasIndex(x => new { x.SellerId, x.SkuId }).IsUnique();
            b.HasIndex(x => new { x.SellerId, x.SellerSkuCode }).IsUnique();
            b.HasOne<Seller>().WithMany().HasForeignKey(x => x.SellerId).OnDelete(DeleteBehavior.Restrict);
            b.HasOne<Sku>().WithMany().HasForeignKey(x => x.SkuId).OnDelete(DeleteBehavior.Restrict);
        });

        model.Entity<InventoryBalance>(b =>
        {
            b.ToTable("InventoryBalances", t => t.HasCheckConstraint("CK_InventoryBalances_Quantities",
                "[Reserved] >= 0 AND [OnHand] >= [Reserved]"));
            b.Ignore(x => x.Available);
            b.HasAlternateKey(x => new { x.Id, x.OfferId });
            b.HasIndex(x => new { x.OfferId, x.LocationId }).IsUnique();
            b.HasOne<SellerOffer>().WithMany().HasForeignKey(x => new { x.OfferId, x.SellerId })
                .HasPrincipalKey(x => new { x.Id, x.SellerId }).OnDelete(DeleteBehavior.Restrict);
            b.HasOne<SellerLocation>().WithMany().HasForeignKey(x => new { x.LocationId, x.SellerId })
                .HasPrincipalKey(x => new { x.Id, x.SellerId }).OnDelete(DeleteBehavior.Restrict);
            b.Property<byte[]>("RowVersion").IsRowVersion();
        });

        model.Entity<OfferStockMovement>(b =>
        {
            b.ToTable("OfferStockMovements");
            b.Property(x => x.ActorId).HasMaxLength(450);
            b.Property(x => x.Reason).HasMaxLength(250);
            b.HasIndex(x => new { x.InventoryBalanceId, x.OccurredAt });
            b.HasOne<InventoryBalance>().WithMany().HasForeignKey(x => x.InventoryBalanceId).OnDelete(DeleteBehavior.Restrict);
        });

        model.Entity<ProductProposal>(b =>
        {
            b.ToTable("ProductProposals", t => t.HasCheckConstraint("CK_ProductProposals_Status", "[Status] BETWEEN 0 AND 2"));
            b.Property(x => x.Name).HasMaxLength(150);
            b.Property(x => x.Slug).HasMaxLength(150);
            b.Property(x => x.Description).HasMaxLength(4000);
            b.HasIndex(x => new { x.SellerId, x.CreatedAt });
            b.HasIndex(x => new { x.Status, x.Id });
            b.HasOne<Seller>().WithMany().HasForeignKey(x => x.SellerId).OnDelete(DeleteBehavior.Restrict);
            b.HasOne<Category>().WithMany().HasForeignKey(x => x.CategoryId).OnDelete(DeleteBehavior.Restrict);
            b.HasOne<Product>().WithMany().HasForeignKey(x => x.ProductId).OnDelete(DeleteBehavior.Restrict);
        });

        model.Entity<Category>(b =>
        {
            b.ToTable("Categories");
            b.Property(x => x.Name).HasMaxLength(100);
            b.HasIndex(x => x.Name).IsUnique();
        });

        model.Entity<Product>(b =>
        {
            b.ToTable("Products");
            b.Property(x => x.SellerId).HasDefaultValue(Seller.FirstPartyId);
            b.HasOne<Seller>().WithMany().HasForeignKey(x => x.SellerId).OnDelete(DeleteBehavior.Restrict);
            b.Property(x => x.Name).HasMaxLength(150);
            b.Property(x => x.Slug).HasMaxLength(150);
            b.Property(x => x.Description).HasMaxLength(4000);
            b.Property(x => x.LegacyPrice).HasColumnName("Price").HasPrecision(18, 2);
            b.HasIndex(x => x.Slug).IsUnique();
            b.HasIndex(x => new { x.Status, x.CategoryId, x.Id });
            b.HasOne<Category>().WithMany().HasForeignKey(x => x.CategoryId).OnDelete(DeleteBehavior.Restrict);
            b.HasMany(x => x.Skus).WithOne().HasForeignKey(x => x.ProductId);
            b.HasMany(x => x.Images).WithOne().HasForeignKey(x => x.ProductId);
        });

        model.Entity<Sku>(b =>
        {
            b.ToTable("Skus", t => t.HasCheckConstraint("CK_Skus_Price", "[PriceVnd] >= 0"));
            b.Property(x => x.Code).HasMaxLength(64);
            b.Property(x => x.Size).HasMaxLength(40);
            b.Property(x => x.Color).HasMaxLength(40);
            b.Property(x => x.PriceVnd).HasPrecision(18, 0);
            b.HasIndex(x => x.Code).IsUnique();
            b.HasOne(x => x.Stock).WithOne().HasForeignKey<StockBalance>(x => x.SkuId);
        });

        model.Entity<ProductImage>(b =>
        {
            b.ToTable("ProductImages");
            b.Property(x => x.FileName).HasMaxLength(50);
            b.Property(x => x.Alt).HasMaxLength(200);
            b.HasIndex(x => new { x.ProductId, x.FileName }).IsUnique();
        });

        model.Entity<ProductReview>(b =>
        {
            b.ToTable("ProductReviews", t => t.HasCheckConstraint("CK_ProductReviews_Rating", "[Rating] BETWEEN 1 AND 5"));
            b.Property(x => x.Content).HasMaxLength(ProductReview.MaxContentLength);
            b.Property(x => x.CustomerId).HasMaxLength(450);
            b.HasIndex(x => new { x.ProductId, x.Id });
            b.HasOne<Product>().WithMany().HasForeignKey(x => x.ProductId).OnDelete(DeleteBehavior.Restrict);
        });

        model.Entity<AboListing>(b =>
        {
            b.ToTable("AboListings");
            b.HasKey(x => new { x.DomainName, x.ItemId });
            b.Property(x => x.DomainName).HasMaxLength(100);
            b.Property(x => x.ItemId).HasMaxLength(32);
            b.Property(x => x.DefaultLanguageTag).HasMaxLength(64);
            b.HasOne<Product>().WithOne().HasForeignKey<AboListing>(x => x.ProductId);
        });

        model.Entity<ProductTranslation>(b =>
        {
            b.ToTable("ProductTranslations");
            b.HasKey(x => new { x.ProductId, x.LanguageTag });
            b.Property(x => x.LanguageTag).HasMaxLength(64);
            b.HasOne<Product>().WithMany().HasForeignKey(x => x.ProductId);
        });

        model.Entity<AboImage>(b =>
        {
            b.ToTable("AboImages");
            b.HasKey(x => x.ImageId);
            b.Property(x => x.ImageId).HasMaxLength(32).UseCollation("Latin1_General_100_BIN2");
            b.Property(x => x.SourcePath).HasMaxLength(256);
            b.Property(x => x.FileName).HasMaxLength(50);
        });

        model.Entity<StockBalance>(b =>
        {
            b.ToTable("StockBalances", t => t.HasCheckConstraint("CK_StockBalances_Quantities",
                "[Reserved] >= 0 AND [OnHand] >= [Reserved] AND [LowThreshold] >= 0"));
            b.HasKey(x => x.SkuId);
            b.Ignore(x => x.Available);
            b.Property<byte[]>("RowVersion").IsRowVersion();
        });

        model.Entity<StockMovement>(b =>
        {
            b.ToTable("StockMovements");
            b.Property(x => x.ActorId).HasMaxLength(450);
            b.Property(x => x.Reason).HasMaxLength(250);
            b.HasIndex(x => new { x.SkuId, x.OccurredAt });
            b.HasOne<Sku>().WithMany().HasForeignKey(x => x.SkuId).OnDelete(DeleteBehavior.Restrict);
        });

        model.Entity<CatalogStockHold>(b =>
        {
            b.ToTable("CatalogStockHolds");
            b.HasIndex(x => new { x.OrderId, x.OfferId }).IsUnique();
            b.HasIndex(x => new { x.OrderId, x.Status });
        });

        model.Entity<VerifiedPurchaseProjection>(b =>
        {
            b.ToTable("VerifiedPurchaseProjections");
            b.Property(x => x.CustomerId).HasMaxLength(450);
            b.HasIndex(x => new { x.CustomerId, x.ProductId });
            b.HasIndex(x => new { x.SellerOrderId, x.ProductId }).IsUnique();
        });
    }
}
