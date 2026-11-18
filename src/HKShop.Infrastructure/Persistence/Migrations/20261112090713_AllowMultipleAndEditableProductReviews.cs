using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace HKShop.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AllowMultipleAndEditableProductReviews : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_ProductReviews_ProductId_CustomerId",
                table: "ProductReviews");

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "UpdatedAt",
                table: "ProductReviews",
                type: "datetimeoffset",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "UpdatedAt",
                table: "ProductReviews");

            migrationBuilder.CreateIndex(
                name: "IX_ProductReviews_ProductId_CustomerId",
                table: "ProductReviews",
                columns: new[] { "ProductId", "CustomerId" },
                unique: true);
        }
    }
}
