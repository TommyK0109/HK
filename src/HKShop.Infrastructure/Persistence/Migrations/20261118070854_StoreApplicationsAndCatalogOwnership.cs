using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace HKShop.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class StoreApplicationsAndCatalogOwnership : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_Sellers_Status",
                table: "Sellers");

            migrationBuilder.AddColumn<long>(
                name: "SellerId",
                table: "Products",
                type: "bigint",
                nullable: false,
                defaultValue: 1L);

            migrationBuilder.Sql(FirstPartySkuTrigger(requireOwner: true));

            migrationBuilder.CreateTable(
                name: "StoreApplications",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    UserId = table.Column<string>(type: "nvarchar(450)", nullable: false),
                    Email = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                    StoreName = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    ProductsJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    Status = table.Column<int>(type: "int", nullable: false),
                    SubmittedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    DecidedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    ReviewerId = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    DeclineReason = table.Column<string>(type: "nvarchar(250)", maxLength: 250, nullable: true),
                    SellerId = table.Column<long>(type: "bigint", nullable: true),
                    ReadAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_StoreApplications", x => x.Id);
                    table.ForeignKey(
                        name: "FK_StoreApplications_AspNetUsers_UserId",
                        column: x => x.UserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_StoreApplications_Sellers_SellerId",
                        column: x => x.SellerId,
                        principalTable: "Sellers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "StoreApplicationVerifications",
                columns: table => new
                {
                    ApplicationId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    NationalId = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    BusinessLicenseNumber = table.Column<string>(type: "nvarchar(max)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_StoreApplicationVerifications", x => x.ApplicationId);
                    table.ForeignKey(
                        name: "FK_StoreApplicationVerifications_StoreApplications_ApplicationId",
                        column: x => x.ApplicationId,
                        principalTable: "StoreApplications",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "StoreVerificationDocuments",
                columns: table => new
                {
                    ApplicationId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Kind = table.Column<int>(type: "int", nullable: false),
                    Data = table.Column<byte[]>(type: "varbinary(max)", nullable: false),
                    ContentType = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_StoreVerificationDocuments", x => new { x.ApplicationId, x.Kind });
                    table.ForeignKey(
                        name: "FK_StoreVerificationDocuments_StoreApplicationVerifications_ApplicationId",
                        column: x => x.ApplicationId,
                        principalTable: "StoreApplicationVerifications",
                        principalColumn: "ApplicationId",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.AddCheckConstraint(
                name: "CK_Sellers_Status",
                table: "Sellers",
                sql: "[Status] BETWEEN 0 AND 3");

            migrationBuilder.CreateIndex(
                name: "IX_Products_SellerId",
                table: "Products",
                column: "SellerId");

            migrationBuilder.CreateIndex(
                name: "IX_StoreApplications_SellerId",
                table: "StoreApplications",
                column: "SellerId",
                unique: true,
                filter: "[SellerId] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_StoreApplications_Status_SubmittedAt",
                table: "StoreApplications",
                columns: new[] { "Status", "SubmittedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_StoreApplications_UserId_SubmittedAt",
                table: "StoreApplications",
                columns: new[] { "UserId", "SubmittedAt" });

            migrationBuilder.AddForeignKey(
                name: "FK_Products_Sellers_SellerId",
                table: "Products",
                column: "SellerId",
                principalTable: "Sellers",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(FirstPartySkuTrigger(requireOwner: false));
            migrationBuilder.Sql("UPDATE dbo.Sellers SET Status = 2 WHERE Status = 3;");

            migrationBuilder.DropForeignKey(
                name: "FK_Products_Sellers_SellerId",
                table: "Products");

            migrationBuilder.DropTable(
                name: "StoreVerificationDocuments");

            migrationBuilder.DropTable(
                name: "StoreApplicationVerifications");

            migrationBuilder.DropTable(
                name: "StoreApplications");

            migrationBuilder.DropCheckConstraint(
                name: "CK_Sellers_Status",
                table: "Sellers");

            migrationBuilder.DropIndex(
                name: "IX_Products_SellerId",
                table: "Products");

            migrationBuilder.DropColumn(
                name: "SellerId",
                table: "Products");

            migrationBuilder.AddCheckConstraint(
                name: "CK_Sellers_Status",
                table: "Sellers",
                sql: "[Status] BETWEEN 0 AND 2");
        }

        private static string FirstPartySkuTrigger(bool requireOwner)
        {
            var ownership = requireOwner ? "JOIN dbo.Products AS product ON product.Id = sku.ProductId AND product.SellerId = 1" : "";
            return $"""
                EXEC(N'CREATE OR ALTER TRIGGER dbo.TR_Skus_FirstPartyOffer ON dbo.Skus AFTER INSERT, UPDATE AS
                BEGIN
                    SET NOCOUNT ON;

                    UPDATE offer SET SellerSkuCode = sku.Code, PriceVnd = sku.PriceVnd,
                        Status = CASE WHEN sku.Active = 1 THEN 1 ELSE 3 END
                    FROM dbo.SellerOffers AS offer
                    JOIN inserted AS sku ON sku.Id = offer.SkuId
                    {ownership}
                    WHERE offer.SellerId = 1;

                    INSERT dbo.SellerOffers (SellerId, SkuId, SellerSkuCode, PriceVnd, Status)
                    SELECT 1, sku.Id, sku.Code, sku.PriceVnd, CASE WHEN sku.Active = 1 THEN 1 ELSE 3 END
                    FROM inserted AS sku
                    {ownership}
                    WHERE NOT EXISTS (SELECT 1 FROM dbo.SellerOffers WHERE SellerId = 1 AND SkuId = sku.Id);

                    INSERT dbo.InventoryBalances (SellerId, OfferId, LocationId, OnHand, Reserved)
                    SELECT 1, offer.Id, 1, COALESCE(stock.OnHand, 0), COALESCE(stock.Reserved, 0)
                    FROM inserted AS sku
                    {ownership}
                    JOIN dbo.SellerOffers AS offer ON offer.SkuId = sku.Id AND offer.SellerId = 1
                    LEFT JOIN dbo.StockBalances AS stock ON stock.SkuId = sku.Id
                    WHERE NOT EXISTS (SELECT 1 FROM dbo.InventoryBalances WHERE OfferId = offer.Id AND LocationId = 1);
                END');
                """;
        }
    }
}
