using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace HKShop.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AboCatalogData : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_ProductImages_FileName",
                table: "ProductImages");

            migrationBuilder.DropIndex(
                name: "IX_ProductImages_ProductId",
                table: "ProductImages");

            migrationBuilder.CreateTable(
                name: "AboImages",
                columns: table => new
                {
                    ImageId = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false, collation: "Latin1_General_100_BIN2"),
                    SourcePath = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                    FileName = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    Width = table.Column<int>(type: "int", nullable: false),
                    Height = table.Column<int>(type: "int", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AboImages", x => x.ImageId);
                });

            migrationBuilder.CreateTable(
                name: "AboListings",
                columns: table => new
                {
                    DomainName = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    ItemId = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    ProductId = table.Column<int>(type: "int", nullable: false),
                    DefaultLanguageTag = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    MetadataJson = table.Column<string>(type: "nvarchar(max)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AboListings", x => new { x.DomainName, x.ItemId });
                    table.ForeignKey(
                        name: "FK_AboListings_Products_ProductId",
                        column: x => x.ProductId,
                        principalTable: "Products",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ProductTranslations",
                columns: table => new
                {
                    ProductId = table.Column<int>(type: "int", nullable: false),
                    LanguageTag = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    Name = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    Description = table.Column<string>(type: "nvarchar(max)", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ProductTranslations", x => new { x.ProductId, x.LanguageTag });
                    table.ForeignKey(
                        name: "FK_ProductTranslations_Products_ProductId",
                        column: x => x.ProductId,
                        principalTable: "Products",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ProductImages_ProductId_FileName",
                table: "ProductImages",
                columns: new[] { "ProductId", "FileName" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_AboListings_ProductId",
                table: "AboListings",
                column: "ProductId",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AboImages");

            migrationBuilder.DropTable(
                name: "AboListings");

            migrationBuilder.DropTable(
                name: "ProductTranslations");

            migrationBuilder.DropIndex(
                name: "IX_ProductImages_ProductId_FileName",
                table: "ProductImages");

            migrationBuilder.CreateIndex(
                name: "IX_ProductImages_FileName",
                table: "ProductImages",
                column: "FileName",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ProductImages_ProductId",
                table: "ProductImages",
                column: "ProductId");
        }
    }
}
