using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace HKShop.Infrastructure.Persistence.Migrations;

public partial class MarketplaceOffersAndInventory : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "SellerLocations",
            columns: table => new
            {
                Id = table.Column<long>(type: "bigint", nullable: false).Annotation("SqlServer:Identity", "1, 1"),
                SellerId = table.Column<long>(type: "bigint", nullable: false),
                Name = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                Active = table.Column<bool>(type: "bit", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_SellerLocations", x => x.Id);
                table.UniqueConstraint("AK_SellerLocations_Id_SellerId", x => new { x.Id, x.SellerId });
                table.ForeignKey("FK_SellerLocations_Sellers_SellerId", x => x.SellerId, "Sellers", "Id",
                    onDelete: ReferentialAction.Restrict);
            });

        migrationBuilder.CreateTable(
            name: "SellerOffers",
            columns: table => new
            {
                Id = table.Column<long>(type: "bigint", nullable: false).Annotation("SqlServer:Identity", "1, 1"),
                SellerId = table.Column<long>(type: "bigint", nullable: false),
                SkuId = table.Column<long>(type: "bigint", nullable: false),
                SellerSkuCode = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                PriceVnd = table.Column<decimal>(type: "decimal(18,0)", precision: 18, scale: 0, nullable: true),
                Status = table.Column<int>(type: "int", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_SellerOffers", x => x.Id);
                table.UniqueConstraint("AK_SellerOffers_Id_SellerId", x => new { x.Id, x.SellerId });
                table.UniqueConstraint("AK_SellerOffers_Id_SkuId", x => new { x.Id, x.SkuId });
                table.CheckConstraint("CK_SellerOffers_Status", "[Status] BETWEEN 0 AND 3");
                table.CheckConstraint("CK_SellerOffers_Price", "[PriceVnd] >= 0");
                table.ForeignKey("FK_SellerOffers_Sellers_SellerId", x => x.SellerId, "Sellers", "Id",
                    onDelete: ReferentialAction.Restrict);
                table.ForeignKey("FK_SellerOffers_Skus_SkuId", x => x.SkuId, "Skus", "Id",
                    onDelete: ReferentialAction.Restrict);
            });

        migrationBuilder.CreateTable(
            name: "InventoryBalances",
            columns: table => new
            {
                Id = table.Column<long>(type: "bigint", nullable: false).Annotation("SqlServer:Identity", "1, 1"),
                SellerId = table.Column<long>(type: "bigint", nullable: false),
                OfferId = table.Column<long>(type: "bigint", nullable: false),
                LocationId = table.Column<long>(type: "bigint", nullable: false),
                OnHand = table.Column<int>(type: "int", nullable: false),
                Reserved = table.Column<int>(type: "int", nullable: false),
                RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_InventoryBalances", x => x.Id);
                table.CheckConstraint("CK_InventoryBalances_Quantities", "[Reserved] >= 0 AND [OnHand] >= [Reserved]");
                table.ForeignKey("FK_InventoryBalances_SellerOffers_OfferId_SellerId", x => new { x.OfferId, x.SellerId },
                    "SellerOffers", new[] { "Id", "SellerId" }, onDelete: ReferentialAction.Restrict);
                table.ForeignKey("FK_InventoryBalances_SellerLocations_LocationId_SellerId", x => new { x.LocationId, x.SellerId },
                    "SellerLocations", new[] { "Id", "SellerId" }, onDelete: ReferentialAction.Restrict);
            });

        migrationBuilder.CreateTable(
            name: "OfferStockMovements",
            columns: table => new
            {
                Id = table.Column<long>(type: "bigint", nullable: false).Annotation("SqlServer:Identity", "1, 1"),
                InventoryBalanceId = table.Column<long>(type: "bigint", nullable: false),
                OnHandDelta = table.Column<int>(type: "int", nullable: false),
                ReservedDelta = table.Column<int>(type: "int", nullable: false),
                Reason = table.Column<string>(type: "nvarchar(250)", maxLength: 250, nullable: false),
                ActorId = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: true),
                OccurredAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_OfferStockMovements", x => x.Id);
                table.ForeignKey("FK_OfferStockMovements_InventoryBalances_InventoryBalanceId", x => x.InventoryBalanceId,
                    "InventoryBalances", "Id", onDelete: ReferentialAction.Restrict);
            });

        migrationBuilder.CreateTable(
            name: "ProductProposals",
            columns: table => new
            {
                Id = table.Column<long>(type: "bigint", nullable: false).Annotation("SqlServer:Identity", "1, 1"),
                SellerId = table.Column<long>(type: "bigint", nullable: false),
                CategoryId = table.Column<long>(type: "bigint", nullable: false),
                Name = table.Column<string>(type: "nvarchar(150)", maxLength: 150, nullable: false),
                Slug = table.Column<string>(type: "nvarchar(150)", maxLength: 150, nullable: false),
                Description = table.Column<string>(type: "nvarchar(4000)", maxLength: 4000, nullable: false),
                Status = table.Column<int>(type: "int", nullable: false),
                ProductId = table.Column<int>(type: "int", nullable: true),
                CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_ProductProposals", x => x.Id);
                table.CheckConstraint("CK_ProductProposals_Status", "[Status] BETWEEN 0 AND 2");
                table.ForeignKey("FK_ProductProposals_Sellers_SellerId", x => x.SellerId, "Sellers", "Id",
                    onDelete: ReferentialAction.Restrict);
                table.ForeignKey("FK_ProductProposals_Categories_CategoryId", x => x.CategoryId, "Categories", "Id",
                    onDelete: ReferentialAction.Restrict);
                table.ForeignKey("FK_ProductProposals_Products_ProductId", x => x.ProductId, "Products", "Id",
                    onDelete: ReferentialAction.Restrict);
            });

        migrationBuilder.CreateIndex("IX_SellerLocations_SellerId", "SellerLocations", "SellerId", unique: true, filter: "[Active] = 1");
        migrationBuilder.CreateIndex("IX_SellerOffers_SkuId", "SellerOffers", "SkuId");
        migrationBuilder.CreateIndex("IX_SellerOffers_SellerId_SkuId", "SellerOffers", new[] { "SellerId", "SkuId" }, unique: true);
        migrationBuilder.CreateIndex("IX_SellerOffers_SellerId_SellerSkuCode", "SellerOffers", new[] { "SellerId", "SellerSkuCode" }, unique: true);
        migrationBuilder.CreateIndex("IX_InventoryBalances_OfferId_LocationId", "InventoryBalances", new[] { "OfferId", "LocationId" }, unique: true);
        migrationBuilder.CreateIndex("IX_InventoryBalances_OfferId_SellerId", "InventoryBalances", new[] { "OfferId", "SellerId" });
        migrationBuilder.CreateIndex("IX_InventoryBalances_LocationId_SellerId", "InventoryBalances", new[] { "LocationId", "SellerId" });
        migrationBuilder.CreateIndex("IX_OfferStockMovements_InventoryBalanceId_OccurredAt", "OfferStockMovements",
            new[] { "InventoryBalanceId", "OccurredAt" });
        migrationBuilder.CreateIndex("IX_ProductProposals_CategoryId", "ProductProposals", "CategoryId");
        migrationBuilder.CreateIndex("IX_ProductProposals_ProductId", "ProductProposals", "ProductId");
        migrationBuilder.CreateIndex("IX_ProductProposals_SellerId_CreatedAt", "ProductProposals", new[] { "SellerId", "CreatedAt" });
        migrationBuilder.CreateIndex("IX_ProductProposals_Status_Id", "ProductProposals", new[] { "Status", "Id" });

        migrationBuilder.InsertData("SellerLocations", new[] { "Id", "SellerId", "Name", "Active" }, new object[] { 1L, 1L, "Kho HKShop", true });
        migrationBuilder.AddColumn<long>("OfferId", "CartItems", type: "bigint", nullable: true);

        // Backfill inside this migration's transaction, before making the offer reference required.
        migrationBuilder.Sql("""
            EXEC(N'
                INSERT dbo.SellerOffers (SellerId, SkuId, SellerSkuCode, PriceVnd, Status)
                SELECT 1, Id, Code, PriceVnd, CASE WHEN Active = 1 THEN 1 ELSE 3 END
                FROM dbo.Skus;

                INSERT dbo.InventoryBalances (SellerId, OfferId, LocationId, OnHand, Reserved)
                SELECT 1, offer.Id, 1, COALESCE(stock.OnHand, 0), COALESCE(stock.Reserved, 0)
                FROM dbo.SellerOffers AS offer
                LEFT JOIN dbo.StockBalances AS stock ON stock.SkuId = offer.SkuId
                WHERE offer.SellerId = 1;

                SET IDENTITY_INSERT dbo.OfferStockMovements ON;
                INSERT dbo.OfferStockMovements (Id, InventoryBalanceId, OnHandDelta, ReservedDelta, Reason, ActorId, OccurredAt)
                SELECT movement.Id, balance.Id, movement.OnHandDelta, movement.ReservedDelta,
                    movement.Reason, movement.ActorId, movement.OccurredAt
                FROM dbo.StockMovements AS movement
                JOIN dbo.SellerOffers AS offer ON offer.SkuId = movement.SkuId AND offer.SellerId = 1
                JOIN dbo.InventoryBalances AS balance ON balance.OfferId = offer.Id AND balance.LocationId = 1;
                SET IDENTITY_INSERT dbo.OfferStockMovements OFF;

                UPDATE item SET OfferId = offer.Id
                FROM dbo.CartItems AS item
                JOIN dbo.SellerOffers AS offer ON offer.SkuId = item.SkuId AND offer.SellerId = 1;
            ');
            """);

        migrationBuilder.DropPrimaryKey("PK_CartItems", "CartItems");
        migrationBuilder.AlterColumn<long>("OfferId", "CartItems", type: "bigint", nullable: false,
            oldClrType: typeof(long), oldType: "bigint", oldNullable: true);
        migrationBuilder.AddPrimaryKey("PK_CartItems", "CartItems", new[] { "CartId", "OfferId" });
        migrationBuilder.CreateIndex("IX_CartItems_OfferId_SkuId", "CartItems", new[] { "OfferId", "SkuId" });
        migrationBuilder.AddForeignKey("FK_CartItems_SellerOffers_OfferId_SkuId", "CartItems", new[] { "OfferId", "SkuId" },
            "SellerOffers", principalColumns: new[] { "Id", "SkuId" }, onDelete: ReferentialAction.Restrict);

        // Existing order workflows still write the SKU and stock tables in this phase.
        migrationBuilder.Sql("""
            EXEC(N'CREATE TRIGGER dbo.TR_Skus_FirstPartyOffer ON dbo.Skus AFTER INSERT, UPDATE AS
            BEGIN
                SET NOCOUNT ON;

                UPDATE offer SET SellerSkuCode = sku.Code, PriceVnd = sku.PriceVnd,
                    Status = CASE WHEN sku.Active = 1 THEN 1 ELSE 3 END
                FROM dbo.SellerOffers AS offer
                JOIN inserted AS sku ON sku.Id = offer.SkuId
                WHERE offer.SellerId = 1;

                INSERT dbo.SellerOffers (SellerId, SkuId, SellerSkuCode, PriceVnd, Status)
                SELECT 1, sku.Id, sku.Code, sku.PriceVnd, CASE WHEN sku.Active = 1 THEN 1 ELSE 3 END
                FROM inserted AS sku
                WHERE NOT EXISTS (SELECT 1 FROM dbo.SellerOffers WHERE SellerId = 1 AND SkuId = sku.Id);

                INSERT dbo.InventoryBalances (SellerId, OfferId, LocationId, OnHand, Reserved)
                SELECT 1, offer.Id, 1, COALESCE(stock.OnHand, 0), COALESCE(stock.Reserved, 0)
                FROM inserted AS sku
                JOIN dbo.SellerOffers AS offer ON offer.SkuId = sku.Id AND offer.SellerId = 1
                LEFT JOIN dbo.StockBalances AS stock ON stock.SkuId = sku.Id
                WHERE NOT EXISTS (SELECT 1 FROM dbo.InventoryBalances WHERE OfferId = offer.Id AND LocationId = 1);
            END');
            """);

        migrationBuilder.Sql("""
            EXEC(N'CREATE TRIGGER dbo.TR_StockBalances_FirstPartyInventory ON dbo.StockBalances AFTER INSERT, UPDATE AS
            BEGIN
                SET NOCOUNT ON;

                UPDATE balance SET OnHand = stock.OnHand, Reserved = stock.Reserved
                FROM dbo.InventoryBalances AS balance
                JOIN dbo.SellerOffers AS offer ON offer.Id = balance.OfferId AND offer.SellerId = 1
                JOIN inserted AS stock ON stock.SkuId = offer.SkuId
                WHERE balance.LocationId = 1;
            END');
            """);

        migrationBuilder.Sql("""
            EXEC(N'CREATE TRIGGER dbo.TR_StockMovements_FirstPartyHistory ON dbo.StockMovements AFTER INSERT AS
            BEGIN
                SET NOCOUNT ON;

                INSERT dbo.OfferStockMovements (InventoryBalanceId, OnHandDelta, ReservedDelta, Reason, ActorId, OccurredAt)
                SELECT balance.Id, movement.OnHandDelta, movement.ReservedDelta, movement.Reason, movement.ActorId, movement.OccurredAt
                FROM inserted AS movement
                JOIN dbo.SellerOffers AS offer ON offer.SkuId = movement.SkuId AND offer.SellerId = 1
                JOIN dbo.InventoryBalances AS balance ON balance.OfferId = offer.Id AND balance.LocationId = 1;
            END');
            """);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
            EXEC(N'
                IF EXISTS (
                    SELECT 1 FROM dbo.CartItems GROUP BY CartId, SkuId HAVING COUNT(*) > 1)
                    THROW 51000, ''Cannot roll back marketplace offers while a cart contains multiple offers for one SKU.'', 1;

                IF EXISTS (
                    SELECT 1 FROM dbo.CartItems AS item
                    JOIN dbo.SellerOffers AS offer ON offer.Id = item.OfferId
                    WHERE offer.SellerId <> 1)
                    THROW 51001, ''Cannot roll back marketplace offers while a cart contains a third-party offer.'', 1;
            ');
            """);

        migrationBuilder.Sql("""
            DROP TRIGGER dbo.TR_StockMovements_FirstPartyHistory;
            DROP TRIGGER dbo.TR_StockBalances_FirstPartyInventory;
            DROP TRIGGER dbo.TR_Skus_FirstPartyOffer;
            """);

        migrationBuilder.DropForeignKey("FK_CartItems_SellerOffers_OfferId_SkuId", "CartItems");
        migrationBuilder.DropIndex("IX_CartItems_OfferId_SkuId", "CartItems");
        migrationBuilder.DropPrimaryKey("PK_CartItems", "CartItems");
        migrationBuilder.DropColumn("OfferId", "CartItems");
        migrationBuilder.AddPrimaryKey("PK_CartItems", "CartItems", new[] { "CartId", "SkuId" });

        migrationBuilder.DropTable("OfferStockMovements");
        migrationBuilder.DropTable("ProductProposals");
        migrationBuilder.DropTable("InventoryBalances");
        migrationBuilder.DropTable("SellerLocations");
        migrationBuilder.DropTable("SellerOffers");
    }
}
