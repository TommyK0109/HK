using HKShop.Domain.Cart;
using HKShop.Domain.Catalog;
using HKShop.Domain.Orders;
using HKShop.Domain.Promotions;
using HKShop.Domain.Sellers;
using HKShop.Domain.Settlement;
using HKShop.Infrastructure.Kafka;
using Microsoft.EntityFrameworkCore;

namespace HKShop.Infrastructure.Persistence.Configurations;

public static class CommerceModelConfiguration
{
    public static void Configure(ModelBuilder model)
    {
        model.Entity<Seller>(b =>
        {
            b.ToTable("Sellers", t => t.HasCheckConstraint("CK_Sellers_Status", "[Status] BETWEEN 0 AND 3"));
            b.Property(x => x.Slug).HasMaxLength(64).IsUnicode(false);
            b.Property(x => x.DisplayName).HasMaxLength(100);
            b.HasIndex(x => x.Slug).IsUnique();
            b.HasData(new Seller
            {
                Id = Seller.FirstPartyId, Slug = "hkshop", DisplayName = "HKShop", Status = SellerStatus.Active,
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
            b.HasOne<ShopUser>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Restrict);
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
            b.HasOne<ShopUser>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Restrict);
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
                Id = SellerLocation.FirstPartyId, SellerId = Seller.FirstPartyId, Name = "Kho HKShop", Active = true
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
            b.ToTable("Skus", t =>
            {
                t.HasCheckConstraint("CK_Skus_Price", "[PriceVnd] >= 0");
                t.HasTrigger("TR_Skus_FirstPartyOffer");
                t.UseSqlOutputClause(false);
            });
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
            b.HasIndex(x => new { x.ProductId, x.Id });
            b.HasOne<Product>().WithMany().HasForeignKey(x => x.ProductId).OnDelete(DeleteBehavior.Restrict);
            b.HasOne<ShopUser>().WithMany().HasForeignKey(x => x.CustomerId).OnDelete(DeleteBehavior.Restrict);
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
            b.HasOne<Order>().WithMany().HasForeignKey(x => x.OrderId).OnDelete(DeleteBehavior.Restrict);
        });

        model.Entity<CustomerAddress>(b =>
        {
            b.ToTable("CustomerAddresses");
            b.Property(x => x.Recipient).HasMaxLength(100);
            b.Property(x => x.Phone).HasMaxLength(20);
            b.Property(x => x.AddressText).HasMaxLength(300);
            b.HasOne<ShopUser>().WithMany().HasForeignKey(x => x.CustomerId).OnDelete(DeleteBehavior.Restrict);
        });
        model.Entity<ShoppingCart>(b =>
        {
            b.ToTable("ShoppingCarts", t => t.HasCheckConstraint("CK_ShoppingCarts_Owner",
                "([CustomerId] IS NULL AND [GuestTokenHash] IS NOT NULL) OR ([CustomerId] IS NOT NULL AND [GuestTokenHash] IS NULL)"));
            b.Property(x => x.GuestTokenHash).HasMaxLength(64);
            b.HasIndex(x => x.GuestTokenHash).IsUnique().HasFilter("[GuestTokenHash] IS NOT NULL");
            b.HasIndex(x => x.CustomerId).IsUnique().HasFilter("[CustomerId] IS NOT NULL");
            b.HasIndex(x => x.UpdatedAt);
            b.HasOne<ShopUser>().WithMany().HasForeignKey(x => x.CustomerId).OnDelete(DeleteBehavior.Restrict);
            b.HasMany(x => x.Items).WithOne().HasForeignKey(x => x.CartId);
            b.Property<byte[]>("RowVersion").IsRowVersion();
        });
        model.Entity<CartItem>(b =>
        {
            b.ToTable("CartItems", t => t.HasCheckConstraint("CK_CartItems_Quantity", "[Quantity] BETWEEN 1 AND 999"));
            b.HasKey(x => new { x.CartId, x.OfferId });
            b.Property(x => x.SeenUnitPriceVnd).HasPrecision(18, 0);
            b.HasOne<Sku>().WithMany().HasForeignKey(x => x.SkuId).OnDelete(DeleteBehavior.Restrict);
            b.HasOne<SellerOffer>().WithMany().HasForeignKey(x => new { x.OfferId, x.SkuId })
                .HasPrincipalKey(x => new { x.Id, x.SkuId }).OnDelete(DeleteBehavior.Restrict);
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
            b.HasIndex(x => x.OrderId).IsUnique();
            b.HasIndex(x => new { x.PromotionId, x.CustomerId }).IsUnique().HasFilter("[Status] <> 2");
            b.HasOne<Promotion>().WithMany().HasForeignKey(x => x.PromotionId).OnDelete(DeleteBehavior.Restrict);
            b.HasOne<Order>().WithMany().HasForeignKey(x => x.OrderId).OnDelete(DeleteBehavior.Restrict);
            b.HasOne<ShopUser>().WithMany().HasForeignKey(x => x.CustomerId).OnDelete(DeleteBehavior.Restrict);
        });

        model.Entity<Order>(b =>
        {
            b.ToTable("Orders");
            b.Property(x => x.Number).HasMaxLength(34);
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
            b.HasIndex(x => new { x.PaymentStatus, x.PaymentDeadline });
            b.HasIndex(x => x.CreatedAt);
            b.HasIndex(x => new { x.SellerId, x.CreatedAt });
            b.HasOne<Seller>().WithMany().HasForeignKey(x => x.SellerId).OnDelete(DeleteBehavior.Restrict);
            b.HasOne<ShopUser>().WithMany().HasForeignKey(x => x.CustomerId).OnDelete(DeleteBehavior.Restrict);
            b.HasMany(x => x.SellerOrders).WithOne().HasForeignKey(x => x.OrderId).OnDelete(DeleteBehavior.Restrict);
            b.HasMany(x => x.Items).WithOne().HasForeignKey(x => x.OrderId).OnDelete(DeleteBehavior.Restrict);
            b.HasMany(x => x.Events).WithOne().HasForeignKey(x => x.OrderId).OnDelete(DeleteBehavior.Restrict);
            b.HasMany(x => x.PaymentAttempts).WithOne().HasForeignKey(x => x.OrderId).OnDelete(DeleteBehavior.Restrict);
            b.HasOne(x => x.Payment).WithOne().HasForeignKey<Payment>(x => x.OrderId).OnDelete(DeleteBehavior.Restrict);
            b.Property<byte[]>("RowVersion").IsRowVersion();
        });
        model.Entity<SellerOrder>(b =>
        {
            b.ToTable("SellerOrders", t => t.HasCheckConstraint("CK_SellerOrders_Amounts",
                "[SubtotalVnd] >= 0 AND [SellerDiscountVnd] >= 0 AND [PlatformDiscountVnd] >= 0 AND [ShippingVnd] >= 0 AND [TotalVnd] >= 0 AND [CommissionVnd] >= 0"));
            b.Ignore(x => x.Transport);
            b.Property(x => x.Number).HasMaxLength(50);
            b.Property(x => x.SubtotalVnd).HasPrecision(18, 0);
            b.Property(x => x.SellerDiscountVnd).HasPrecision(18, 0);
            b.Property(x => x.PlatformDiscountVnd).HasPrecision(18, 0);
            b.Property(x => x.ShippingVnd).HasPrecision(18, 0);
            b.Property(x => x.TotalVnd).HasPrecision(18, 0);
            b.Property(x => x.CommissionVnd).HasPrecision(18, 0);
            b.HasAlternateKey(x => new { x.Id, x.SellerId });
            b.HasAlternateKey(x => new { x.Id, x.OrderId });
            b.HasAlternateKey(x => new { x.Id, x.OrderId, x.SellerId });
            b.HasIndex(x => x.Number).IsUnique();
            b.HasIndex(x => new { x.OrderId, x.SellerId }).IsUnique();
            b.HasIndex(x => new { x.SellerId, x.Status, x.CreatedAt });
            b.HasOne<Seller>().WithMany().HasForeignKey(x => x.SellerId).OnDelete(DeleteBehavior.Restrict);
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
            b.HasAlternateKey(x => new { x.Id, x.OfferId });
            b.HasAlternateKey(x => new { x.Id, x.SellerOrderId });
            b.HasIndex(x => new { x.SellerOrderId, x.OfferId });
            b.HasOne<Sku>().WithMany().HasForeignKey(x => x.SkuId).OnDelete(DeleteBehavior.Restrict);
            b.HasOne<SellerOffer>().WithMany().HasForeignKey(x => new { x.OfferId, x.SellerId })
                .HasPrincipalKey(x => new { x.Id, x.SellerId }).OnDelete(DeleteBehavior.Restrict);
            b.HasOne<SellerOffer>().WithMany().HasForeignKey(x => new { x.OfferId, x.SkuId })
                .HasPrincipalKey(x => new { x.Id, x.SkuId }).OnDelete(DeleteBehavior.Restrict);
            b.HasOne<SellerOrder>().WithMany().HasForeignKey(x => new { x.SellerOrderId, x.OrderId, x.SellerId })
                .HasPrincipalKey(x => new { x.Id, x.OrderId, x.SellerId }).OnDelete(DeleteBehavior.Restrict);
        });
        model.Entity<OrderEvent>(b =>
        {
            b.ToTable("OrderEvents");
            b.Property(x => x.Note).HasMaxLength(250);
        });
        model.Entity<StockReservation>(b =>
        {
            b.ToTable("StockReservations", t => t.HasCheckConstraint("CK_StockReservations_Quantity", "[Quantity] > 0"));
            b.HasIndex(x => new { x.OrderId, x.SkuId });
            b.HasIndex(x => new { x.OrderItemId, x.InventoryBalanceId }).IsUnique().HasFilter("[OrderItemId] IS NOT NULL");
            b.HasIndex(x => new { x.Status, x.ExpiresAt });
            b.HasOne<Sku>().WithMany().HasForeignKey(x => x.SkuId).OnDelete(DeleteBehavior.Restrict);
            b.HasOne<Order>().WithMany().HasForeignKey(x => x.OrderId).OnDelete(DeleteBehavior.Restrict);
            b.HasOne<OrderItem>().WithMany().HasForeignKey(x => new { x.OrderItemId, x.OfferId })
                .HasPrincipalKey(x => new { x.Id, x.OfferId }).OnDelete(DeleteBehavior.Restrict);
            b.HasOne<InventoryBalance>().WithMany().HasForeignKey(x => new { x.InventoryBalanceId, x.OfferId })
                .HasPrincipalKey(x => new { x.Id, x.OfferId }).OnDelete(DeleteBehavior.Restrict);
        });
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
            b.HasOne<SellerOrder>().WithMany().HasForeignKey(x => new { x.SellerOrderId, x.OrderId })
                .HasPrincipalKey(x => new { x.Id, x.OrderId }).OnDelete(DeleteBehavior.Restrict);
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
            b.HasOne<Order>().WithMany().HasForeignKey(x => x.OrderId).OnDelete(DeleteBehavior.Restrict);
            b.HasOne<PaymentAttempt>().WithMany().HasForeignKey(x => x.PaymentAttemptId).OnDelete(DeleteBehavior.Restrict);
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
            b.HasOne<Order>().WithMany().HasForeignKey(x => x.OrderId).OnDelete(DeleteBehavior.Restrict);
            b.HasOne<PaymentAttempt>().WithMany().HasForeignKey(x => new { x.PaymentAttemptId, x.OrderId })
                .HasPrincipalKey(x => new { x.Id, x.OrderId }).OnDelete(DeleteBehavior.Restrict);
            b.HasOne<SellerOrder>().WithMany().HasForeignKey(x => new { x.SellerOrderId, x.OrderId })
                .HasPrincipalKey(x => new { x.Id, x.OrderId }).OnDelete(DeleteBehavior.Restrict);
            b.HasOne<ReturnRequest>().WithMany().HasForeignKey(x => new { x.ReturnId, x.SellerOrderId })
                .HasPrincipalKey(x => new { x.Id, x.SellerOrderId }).OnDelete(DeleteBehavior.Restrict);
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
            b.HasOne<OrderItem>().WithMany().HasForeignKey(x => new { x.OrderItemId, x.SellerOrderId })
                .HasPrincipalKey(x => new { x.Id, x.SellerOrderId }).OnDelete(DeleteBehavior.Restrict);
        });
        model.Entity<LedgerAccount>(b =>
        {
            b.ToTable("LedgerAccounts", t => t.HasCheckConstraint("CK_LedgerAccounts_Scope",
                "([Type] BETWEEN 6 AND 8 AND [SellerId] IS NOT NULL) OR (([Type] BETWEEN 0 AND 5 OR [Type] BETWEEN 9 AND 11) AND [SellerId] IS NULL)"));
            b.HasIndex(x => x.Type).IsUnique().HasFilter("[SellerId] IS NULL");
            b.HasIndex(x => new { x.SellerId, x.Type }).IsUnique().HasFilter("[SellerId] IS NOT NULL");
            b.HasOne<Seller>().WithMany().HasForeignKey(x => x.SellerId).OnDelete(DeleteBehavior.Restrict);
        });
        model.Entity<LedgerJournal>(b =>
        {
            b.ToTable("LedgerJournals", t => t.HasTrigger("TR_LedgerJournals_AppendOnly"));
            b.Property(x => x.BusinessKey).HasMaxLength(120);
            b.HasIndex(x => x.BusinessKey).IsUnique();
            b.HasIndex(x => new { x.SellerOrderId, x.Type });
            b.HasOne<SellerOrder>().WithMany().HasForeignKey(x => x.SellerOrderId).OnDelete(DeleteBehavior.Restrict);
            b.HasOne<PaymentAttempt>().WithMany().HasForeignKey(x => x.PaymentAttemptId).OnDelete(DeleteBehavior.Restrict);
            b.HasOne<Refund>().WithMany().HasForeignKey(x => x.RefundId).OnDelete(DeleteBehavior.Restrict);
            b.HasOne<Payout>().WithMany().HasForeignKey(x => x.PayoutId).OnDelete(DeleteBehavior.Restrict);
            b.HasMany(x => x.Entries).WithOne().HasForeignKey(x => x.JournalId).OnDelete(DeleteBehavior.Restrict);
        });
        model.Entity<LedgerEntry>(b =>
        {
            b.ToTable("LedgerEntries", t =>
            {
                t.HasTrigger("TR_LedgerEntries_AppendOnly");
                t.HasCheckConstraint("CK_LedgerEntries_AmountAndSide", "[AmountVnd] > 0 AND [Side] IN (0, 1)");
            });
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
            b.HasOne<Seller>().WithMany().HasForeignKey(x => x.SellerId).OnDelete(DeleteBehavior.Restrict);
            b.HasOne<ShopUser>().WithMany().HasForeignKey(x => x.RequestedBy).OnDelete(DeleteBehavior.Restrict);
            b.Property<byte[]>("RowVersion").IsRowVersion();
        });
        model.Entity<SimulatedPayoutTransfer>(b =>
        {
            b.ToTable("SimulatedPayoutTransfers", t => t.HasCheckConstraint("CK_SimulatedPayoutTransfers_Amount", "[AmountVnd] > 0"));
            b.HasKey(x => x.PayoutId);
            b.Property(x => x.ProviderReference).HasMaxLength(120);
            b.Property(x => x.AmountVnd).HasPrecision(18, 0);
            b.HasOne<Payout>().WithOne().HasForeignKey<SimulatedPayoutTransfer>(x => x.PayoutId).OnDelete(DeleteBehavior.Restrict);
        });
        model.Entity<OutboxMessage>(b =>
        {
            b.ToTable("OutboxMessages");
            b.Property(x => x.EventKey).HasMaxLength(120);
            b.Property(x => x.Recipient).HasMaxLength(256);
            b.Property(x => x.Subject).HasMaxLength(200);
            b.Property(x => x.Body).HasMaxLength(2000);
            b.HasIndex(x => x.EventKey).IsUnique();
            b.HasIndex(x => new { x.SentAt, x.NextAttemptAt });
        });
        model.Entity<CheckoutRequest>(b =>
        {
            b.ToTable("CheckoutRequests");
            b.Property(r => r.Id).ValueGeneratedNever();
            b.Property(r => r.CustomerId).HasMaxLength(450);
            b.Property(r => r.Error).HasMaxLength(500);
            b.HasIndex(r => new { r.Status, r.PublishedAt, r.CreatedAt });
            b.HasIndex(r => new { r.Status, r.ExpiresAt });
            b.HasOne<Order>().WithMany().HasForeignKey(r => r.OrderId).OnDelete(DeleteBehavior.Restrict);
        });
        model.Entity<IntegrationOutboxEvent>(b =>
        {
            b.ToTable("IntegrationOutbox");
            b.Property(x => x.AggregateType).HasMaxLength(60);
            b.Property(x => x.AggregateId).HasMaxLength(80);
            b.Property(x => x.EventType).HasMaxLength(80);
            b.Property(x => x.IdempotencyKey).HasMaxLength(160);
            b.HasIndex(x => x.EventId).IsUnique();
            b.HasIndex(x => x.IdempotencyKey).IsUnique();
            b.HasIndex(x => new { x.PublishedAt, x.OccurredAt });
        });
        model.Entity<ConsumedIntegrationEvent>(b =>
        {
            b.ToTable("ConsumedIntegrationEvents");
            b.Property(x => x.ConsumerName).HasMaxLength(80);
            b.Property(x => x.EventKey).HasMaxLength(160);
            b.HasIndex(x => new { x.ConsumerName, x.EventKey }).IsUnique();
        });
        model.Entity<AuditEntry>(b =>
        {
            b.ToTable("AuditEntries");
            b.Property(x => x.ActorId).HasMaxLength(450);
            b.Property(x => x.Action).HasMaxLength(100);
            b.Property(x => x.Target).HasMaxLength(150);
            b.Property(x => x.Reason).HasMaxLength(250);
            b.HasIndex(x => x.OccurredAt);
            b.HasIndex(x => new { x.SellerId, x.OccurredAt });
            b.HasOne<Seller>().WithMany().HasForeignKey(x => x.SellerId).OnDelete(DeleteBehavior.Restrict);
        });
    }
}
