using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace HKShop.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class MarketplaceSellerOnboarding : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "SellerId",
                table: "Orders",
                type: "bigint",
                nullable: false,
                defaultValue: 1L);

            migrationBuilder.AddColumn<long>(
                name: "SellerId",
                table: "AuditEntries",
                type: "bigint",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "Sellers",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    Slug = table.Column<string>(type: "varchar(64)", unicode: false, maxLength: 64, nullable: false),
                    DisplayName = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    Status = table.Column<int>(type: "int", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Sellers", x => x.Id);
                    table.CheckConstraint("CK_Sellers_Status", "[Status] BETWEEN 0 AND 2");
                });

            migrationBuilder.CreateTable(
                name: "SellerInvitations",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    SellerId = table.Column<long>(type: "bigint", nullable: false),
                    Email = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                    NormalizedEmail = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                    Role = table.Column<int>(type: "int", nullable: false),
                    TokenHash = table.Column<string>(type: "varchar(64)", unicode: false, maxLength: 64, nullable: false),
                    ExpiresAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    AcceptedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    RevokedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SellerInvitations", x => x.Id);
                    table.CheckConstraint("CK_SellerInvitations_Role", "[Role] BETWEEN 1 AND 3");
                    table.ForeignKey(
                        name: "FK_SellerInvitations_Sellers_SellerId",
                        column: x => x.SellerId,
                        principalTable: "Sellers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "SellerMemberships",
                columns: table => new
                {
                    SellerId = table.Column<long>(type: "bigint", nullable: false),
                    UserId = table.Column<string>(type: "nvarchar(450)", nullable: false),
                    Email = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                    Role = table.Column<int>(type: "int", nullable: false),
                    Status = table.Column<int>(type: "int", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SellerMemberships", x => new { x.SellerId, x.UserId })
                        .Annotation("SqlServer:Clustered", false);
                    table.CheckConstraint("CK_SellerMemberships_Role", "[Role] BETWEEN 0 AND 3");
                    table.CheckConstraint("CK_SellerMemberships_Status", "[Status] BETWEEN 0 AND 1");
                    table.ForeignKey(
                        name: "FK_SellerMemberships_AspNetUsers_UserId",
                        column: x => x.UserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_SellerMemberships_Sellers_SellerId",
                        column: x => x.SellerId,
                        principalTable: "Sellers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.InsertData(
                table: "Sellers",
                columns: new[] { "Id", "CreatedAt", "DisplayName", "Slug", "Status" },
                values: new object[] { 1L, new DateTimeOffset(new DateTime(1970, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified), new TimeSpan(0, 0, 0, 0, 0)), "HKShop", "hkshop", 1 });

            migrationBuilder.Sql("""
                EXEC(N'UPDATE a SET SellerId = o.SellerId
                FROM AuditEntries a JOIN Orders o ON o.Number = a.Target
                WHERE a.Action IN (N''Hủy đơn'', N''Đóng gói'', N''Xuất hàng'', N''Xác nhận giao hàng'',
                    N''Sự cố giao hàng'', N''Nhận lại bưu kiện'', N''Đối soát SePay'');

                UPDATE a SET SellerId = o.SellerId
                FROM AuditEntries a
                JOIN ReturnRequests r ON CONVERT(nvarchar(36), r.Id) = a.Target
                JOIN Orders o ON o.Id = r.OrderId
                WHERE a.Action IN (N''Duyệt trả hàng'', N''Kiểm hàng trả'');

                UPDATE a SET SellerId = o.SellerId
                FROM AuditEntries a
                JOIN Refunds r ON r.Id = TRY_CONVERT(bigint, a.Target)
                JOIN Orders o ON o.Id = r.OrderId
                WHERE a.Action = N''Hoàn tiền mô phỏng'';');
                """);

            migrationBuilder.CreateIndex(
                name: "IX_Orders_SellerId_CreatedAt",
                table: "Orders",
                columns: new[] { "SellerId", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_AuditEntries_SellerId_OccurredAt",
                table: "AuditEntries",
                columns: new[] { "SellerId", "OccurredAt" });

            migrationBuilder.CreateIndex(
                name: "IX_SellerInvitations_SellerId_NormalizedEmail",
                table: "SellerInvitations",
                columns: new[] { "SellerId", "NormalizedEmail" });

            migrationBuilder.CreateIndex(
                name: "IX_SellerInvitations_TokenHash",
                table: "SellerInvitations",
                column: "TokenHash",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_SellerMemberships_UserId",
                table: "SellerMemberships",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "IX_Sellers_Slug",
                table: "Sellers",
                column: "Slug",
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "FK_AuditEntries_Sellers_SellerId",
                table: "AuditEntries",
                column: "SellerId",
                principalTable: "Sellers",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_Orders_Sellers_SellerId",
                table: "Orders",
                column: "SellerId",
                principalTable: "Sellers",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_AuditEntries_Sellers_SellerId",
                table: "AuditEntries");

            migrationBuilder.DropForeignKey(
                name: "FK_Orders_Sellers_SellerId",
                table: "Orders");

            migrationBuilder.DropTable(
                name: "SellerInvitations");

            migrationBuilder.DropTable(
                name: "SellerMemberships");

            migrationBuilder.DropTable(
                name: "Sellers");

            migrationBuilder.DropIndex(
                name: "IX_Orders_SellerId_CreatedAt",
                table: "Orders");

            migrationBuilder.DropIndex(
                name: "IX_AuditEntries_SellerId_OccurredAt",
                table: "AuditEntries");

            migrationBuilder.DropColumn(
                name: "SellerId",
                table: "Orders");

            migrationBuilder.DropColumn(
                name: "SellerId",
                table: "AuditEntries");
        }
    }
}
