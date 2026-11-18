using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace HKShop.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class MarketplaceFulfillmentReturnsAndRefunds : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Refunds_PaymentAttempts_PaymentAttemptId",
                table: "Refunds");

            migrationBuilder.DropForeignKey(
                name: "FK_Refunds_ReturnRequests_ReturnId",
                table: "Refunds");

            migrationBuilder.DropForeignKey(
                name: "FK_Refunds_SellerOrders_SellerOrderId",
                table: "Refunds");

            migrationBuilder.DropForeignKey(
                name: "FK_ReturnItems_OrderItems_OrderItemId",
                table: "ReturnItems");

            migrationBuilder.DropForeignKey(
                name: "FK_ReturnItems_ReturnRequests_ReturnId",
                table: "ReturnItems");

            migrationBuilder.DropForeignKey(
                name: "FK_ReturnRequests_Orders_OrderId",
                table: "ReturnRequests");

            migrationBuilder.DropForeignKey(
                name: "FK_Shipments_Orders_OrderId",
                table: "Shipments");

            migrationBuilder.DropPrimaryKey(
                name: "PK_Shipments",
                table: "Shipments");

            migrationBuilder.DropIndex(
                name: "IX_ReturnItems_OrderItemId",
                table: "ReturnItems");

            migrationBuilder.DropIndex(
                name: "IX_Refunds_PaymentAttemptId",
                table: "Refunds");

            migrationBuilder.DropIndex(
                name: "IX_Refunds_SellerOrderId",
                table: "Refunds");

            migrationBuilder.DropCheckConstraint(
                name: "CK_Refunds_Amount",
                table: "Refunds");

            migrationBuilder.AddColumn<Guid>(
                name: "SellerOrderId",
                table: "Shipments",
                type: "uniqueidentifier",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "DeliveredAt",
                table: "SellerOrders",
                type: "datetimeoffset",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "SellerOrderId",
                table: "ReturnRequests",
                type: "uniqueidentifier",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            migrationBuilder.AddColumn<Guid>(
                name: "SellerOrderId",
                table: "ReturnItems",
                type: "uniqueidentifier",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            migrationBuilder.AddColumn<int>(
                name: "Attempts",
                table: "Refunds",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "Issue",
                table: "Refunds",
                type: "nvarchar(250)",
                maxLength: 250,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "NextAttemptAt",
                table: "Refunds",
                type: "datetimeoffset",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ProviderReference",
                table: "Refunds",
                type: "nvarchar(120)",
                maxLength: 120,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<decimal>(
                name: "ShippingVnd",
                table: "Refunds",
                type: "decimal(18,0)",
                precision: 18,
                scale: 0,
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<bool>(
                name: "Simulated",
                table: "Refunds",
                type: "bit",
                nullable: false,
                defaultValue: false);

            // Phase 3 only shipped or delivered single-seller orders. Preserve those shipments and returns.
            migrationBuilder.Sql("""
                EXEC(N'
                UPDATE child
                SET child.DeliveredAt = purchase.DeliveredAt
                FROM dbo.SellerOrders child
                INNER JOIN dbo.Orders purchase ON purchase.Id = child.OrderId
                WHERE child.Status = 4 AND purchase.DeliveredAt IS NOT NULL;

                UPDATE shipment
                SET shipment.SellerOrderId = child.Id
                FROM dbo.Shipments shipment
                INNER JOIN dbo.Orders purchase ON purchase.Id = shipment.OrderId
                INNER JOIN dbo.SellerOrders child ON child.OrderId = purchase.Id
                    AND child.SellerId = COALESCE(purchase.SellerId, 1);

                UPDATE request
                SET request.SellerOrderId = COALESCE(firstLine.SellerOrderId, legacyChild.Id)
                FROM dbo.ReturnRequests request
                INNER JOIN dbo.Orders purchase ON purchase.Id = request.OrderId
                LEFT JOIN dbo.SellerOrders legacyChild ON legacyChild.OrderId = purchase.Id
                    AND legacyChild.SellerId = COALESCE(purchase.SellerId, 1)
                OUTER APPLY (
                    SELECT TOP (1) item.SellerOrderId
                    FROM dbo.ReturnItems line
                    INNER JOIN dbo.OrderItems item ON item.Id = line.OrderItemId
                    WHERE line.ReturnId = request.Id AND item.OrderId = request.OrderId
                    ORDER BY line.Id
                ) firstLine;

                UPDATE line
                SET line.SellerOrderId = item.SellerOrderId
                FROM dbo.ReturnItems line
                INNER JOIN dbo.OrderItems item ON item.Id = line.OrderItemId;

                UPDATE refund
                SET refund.SellerOrderId = request.SellerOrderId
                FROM dbo.Refunds refund
                INNER JOIN dbo.ReturnRequests request ON request.Id = refund.ReturnId;

                UPDATE refund
                SET refund.PaymentAttemptId = capture.Id
                FROM dbo.Refunds refund
                INNER JOIN dbo.PaymentAttempts capture ON capture.OrderId = refund.OrderId
                    AND capture.AppliedToOrder = 1 AND capture.Status = 2
                WHERE refund.PaymentAttemptId IS NULL;

                UPDATE refund
                SET refund.SellerOrderId = child.Id, refund.ShippingVnd = child.ShippingVnd
                FROM dbo.Refunds refund
                INNER JOIN dbo.Orders purchase ON purchase.Id = refund.OrderId
                INNER JOIN dbo.SellerOrders child ON child.OrderId = purchase.Id AND child.SellerId = purchase.SellerId
                WHERE refund.ReturnId IS NULL AND refund.AmountVnd = child.TotalVnd
                    AND (refund.BusinessKey LIKE N''%:cancelled'' OR refund.BusinessKey LIKE N''%:undeliverable'');

                UPDATE dbo.Refunds SET Simulated = 1 WHERE Status = 1;
                ');
                """);

            migrationBuilder.AddPrimaryKey(
                name: "PK_Shipments",
                table: "Shipments",
                column: "SellerOrderId");

            migrationBuilder.AddUniqueConstraint(
                name: "AK_SellerOrders_Id_OrderId",
                table: "SellerOrders",
                columns: new[] { "Id", "OrderId" });

            migrationBuilder.AddUniqueConstraint(
                name: "AK_ReturnRequests_Id_SellerOrderId",
                table: "ReturnRequests",
                columns: new[] { "Id", "SellerOrderId" });

            migrationBuilder.AddUniqueConstraint(
                name: "AK_PaymentAttempts_Id_OrderId",
                table: "PaymentAttempts",
                columns: new[] { "Id", "OrderId" });

            migrationBuilder.AddUniqueConstraint(
                name: "AK_OrderItems_Id_SellerOrderId",
                table: "OrderItems",
                columns: new[] { "Id", "SellerOrderId" });

            migrationBuilder.CreateTable(
                name: "RefundItems",
                columns: table => new
                {
                    RefundId = table.Column<long>(type: "bigint", nullable: false),
                    OrderItemId = table.Column<long>(type: "bigint", nullable: false),
                    SellerOrderId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Quantity = table.Column<int>(type: "int", nullable: false),
                    AmountVnd = table.Column<decimal>(type: "decimal(18,0)", precision: 18, scale: 0, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RefundItems", x => new { x.RefundId, x.OrderItemId });
                    table.CheckConstraint("CK_RefundItems_Amounts", "[Quantity] > 0 AND [AmountVnd] >= 0");
                    table.ForeignKey(
                        name: "FK_RefundItems_OrderItems_OrderItemId_SellerOrderId",
                        columns: x => new { x.OrderItemId, x.SellerOrderId },
                        principalTable: "OrderItems",
                        principalColumns: new[] { "Id", "SellerOrderId" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_RefundItems_Refunds_RefundId",
                        column: x => x.RefundId,
                        principalTable: "Refunds",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Shipments_OrderId",
                table: "Shipments",
                column: "OrderId");

            migrationBuilder.CreateIndex(
                name: "IX_Shipments_SellerOrderId_OrderId",
                table: "Shipments",
                columns: new[] { "SellerOrderId", "OrderId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ReturnRequests_SellerOrderId_OrderId",
                table: "ReturnRequests",
                columns: new[] { "SellerOrderId", "OrderId" });

            migrationBuilder.CreateIndex(
                name: "IX_ReturnRequests_SellerOrderId_Status",
                table: "ReturnRequests",
                columns: new[] { "SellerOrderId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_ReturnItems_OrderItemId_SellerOrderId",
                table: "ReturnItems",
                columns: new[] { "OrderItemId", "SellerOrderId" });

            migrationBuilder.CreateIndex(
                name: "IX_ReturnItems_ReturnId_SellerOrderId",
                table: "ReturnItems",
                columns: new[] { "ReturnId", "SellerOrderId" });

            migrationBuilder.CreateIndex(
                name: "IX_Refunds_Id_SellerOrderId",
                table: "Refunds",
                columns: new[] { "Id", "SellerOrderId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Refunds_PaymentAttemptId_OrderId",
                table: "Refunds",
                columns: new[] { "PaymentAttemptId", "OrderId" });

            migrationBuilder.CreateIndex(
                name: "IX_Refunds_ReturnId_SellerOrderId",
                table: "Refunds",
                columns: new[] { "ReturnId", "SellerOrderId" });

            migrationBuilder.CreateIndex(
                name: "IX_Refunds_SellerOrderId_OrderId",
                table: "Refunds",
                columns: new[] { "SellerOrderId", "OrderId" });

            migrationBuilder.CreateIndex(
                name: "IX_Refunds_Status_NextAttemptAt",
                table: "Refunds",
                columns: new[] { "Status", "NextAttemptAt" });

            migrationBuilder.AddCheckConstraint(
                name: "CK_Refunds_Amount",
                table: "Refunds",
                sql: "[AmountVnd] > 0 AND [ShippingVnd] BETWEEN 0 AND [AmountVnd] AND ([ReturnId] IS NULL OR [SellerOrderId] IS NOT NULL)");

            migrationBuilder.CreateIndex(
                name: "IX_RefundItems_OrderItemId_SellerOrderId",
                table: "RefundItems",
                columns: new[] { "OrderItemId", "SellerOrderId" });

            // A nullable seller order permits checkout-level refunds. The SQL foreign key
            // uses the unique index so a seller refund line must still match its refund's seller order.
            migrationBuilder.AddForeignKey(
                name: "FK_RefundItems_Refunds_RefundId_SellerOrderId",
                table: "RefundItems",
                columns: new[] { "RefundId", "SellerOrderId" },
                principalTable: "Refunds",
                principalColumns: new[] { "Id", "SellerOrderId" },
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_Refunds_PaymentAttempts_PaymentAttemptId_OrderId",
                table: "Refunds",
                columns: new[] { "PaymentAttemptId", "OrderId" },
                principalTable: "PaymentAttempts",
                principalColumns: new[] { "Id", "OrderId" },
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_Refunds_ReturnRequests_ReturnId_SellerOrderId",
                table: "Refunds",
                columns: new[] { "ReturnId", "SellerOrderId" },
                principalTable: "ReturnRequests",
                principalColumns: new[] { "Id", "SellerOrderId" },
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_Refunds_SellerOrders_SellerOrderId_OrderId",
                table: "Refunds",
                columns: new[] { "SellerOrderId", "OrderId" },
                principalTable: "SellerOrders",
                principalColumns: new[] { "Id", "OrderId" },
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_ReturnItems_OrderItems_OrderItemId_SellerOrderId",
                table: "ReturnItems",
                columns: new[] { "OrderItemId", "SellerOrderId" },
                principalTable: "OrderItems",
                principalColumns: new[] { "Id", "SellerOrderId" },
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_ReturnItems_ReturnRequests_ReturnId_SellerOrderId",
                table: "ReturnItems",
                columns: new[] { "ReturnId", "SellerOrderId" },
                principalTable: "ReturnRequests",
                principalColumns: new[] { "Id", "SellerOrderId" },
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_ReturnRequests_SellerOrders_SellerOrderId_OrderId",
                table: "ReturnRequests",
                columns: new[] { "SellerOrderId", "OrderId" },
                principalTable: "SellerOrders",
                principalColumns: new[] { "Id", "OrderId" },
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_Shipments_SellerOrders_SellerOrderId_OrderId",
                table: "Shipments",
                columns: new[] { "SellerOrderId", "OrderId" },
                principalTable: "SellerOrders",
                principalColumns: new[] { "Id", "OrderId" },
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                IF EXISTS (SELECT OrderId FROM dbo.Shipments GROUP BY OrderId HAVING COUNT(*) > 1)
                    THROW 51000, 'Phase 3 cannot represent orders with multiple shipments.', 1;
                """);

            migrationBuilder.DropForeignKey(
                name: "FK_Refunds_PaymentAttempts_PaymentAttemptId_OrderId",
                table: "Refunds");

            migrationBuilder.DropForeignKey(
                name: "FK_Refunds_ReturnRequests_ReturnId_SellerOrderId",
                table: "Refunds");

            migrationBuilder.DropForeignKey(
                name: "FK_Refunds_SellerOrders_SellerOrderId_OrderId",
                table: "Refunds");

            migrationBuilder.DropForeignKey(
                name: "FK_ReturnItems_OrderItems_OrderItemId_SellerOrderId",
                table: "ReturnItems");

            migrationBuilder.DropForeignKey(
                name: "FK_ReturnItems_ReturnRequests_ReturnId_SellerOrderId",
                table: "ReturnItems");

            migrationBuilder.DropForeignKey(
                name: "FK_ReturnRequests_SellerOrders_SellerOrderId_OrderId",
                table: "ReturnRequests");

            migrationBuilder.DropForeignKey(
                name: "FK_Shipments_SellerOrders_SellerOrderId_OrderId",
                table: "Shipments");

            migrationBuilder.DropTable(
                name: "RefundItems");

            migrationBuilder.DropPrimaryKey(
                name: "PK_Shipments",
                table: "Shipments");

            migrationBuilder.DropIndex(
                name: "IX_Shipments_OrderId",
                table: "Shipments");

            migrationBuilder.DropIndex(
                name: "IX_Shipments_SellerOrderId_OrderId",
                table: "Shipments");

            migrationBuilder.DropUniqueConstraint(
                name: "AK_SellerOrders_Id_OrderId",
                table: "SellerOrders");

            migrationBuilder.DropUniqueConstraint(
                name: "AK_ReturnRequests_Id_SellerOrderId",
                table: "ReturnRequests");

            migrationBuilder.DropIndex(
                name: "IX_ReturnRequests_SellerOrderId_OrderId",
                table: "ReturnRequests");

            migrationBuilder.DropIndex(
                name: "IX_ReturnRequests_SellerOrderId_Status",
                table: "ReturnRequests");

            migrationBuilder.DropIndex(
                name: "IX_ReturnItems_OrderItemId_SellerOrderId",
                table: "ReturnItems");

            migrationBuilder.DropIndex(
                name: "IX_ReturnItems_ReturnId_SellerOrderId",
                table: "ReturnItems");

            migrationBuilder.DropIndex(
                name: "IX_Refunds_Id_SellerOrderId",
                table: "Refunds");

            migrationBuilder.DropIndex(
                name: "IX_Refunds_PaymentAttemptId_OrderId",
                table: "Refunds");

            migrationBuilder.DropIndex(
                name: "IX_Refunds_ReturnId_SellerOrderId",
                table: "Refunds");

            migrationBuilder.DropIndex(
                name: "IX_Refunds_SellerOrderId_OrderId",
                table: "Refunds");

            migrationBuilder.DropIndex(
                name: "IX_Refunds_Status_NextAttemptAt",
                table: "Refunds");

            migrationBuilder.DropCheckConstraint(
                name: "CK_Refunds_Amount",
                table: "Refunds");

            migrationBuilder.DropUniqueConstraint(
                name: "AK_PaymentAttempts_Id_OrderId",
                table: "PaymentAttempts");

            migrationBuilder.DropUniqueConstraint(
                name: "AK_OrderItems_Id_SellerOrderId",
                table: "OrderItems");

            migrationBuilder.DropColumn(
                name: "SellerOrderId",
                table: "Shipments");

            migrationBuilder.DropColumn(
                name: "DeliveredAt",
                table: "SellerOrders");

            migrationBuilder.DropColumn(
                name: "SellerOrderId",
                table: "ReturnRequests");

            migrationBuilder.DropColumn(
                name: "SellerOrderId",
                table: "ReturnItems");

            migrationBuilder.DropColumn(
                name: "Attempts",
                table: "Refunds");

            migrationBuilder.DropColumn(
                name: "Issue",
                table: "Refunds");

            migrationBuilder.DropColumn(
                name: "NextAttemptAt",
                table: "Refunds");

            migrationBuilder.DropColumn(
                name: "ProviderReference",
                table: "Refunds");

            migrationBuilder.DropColumn(
                name: "ShippingVnd",
                table: "Refunds");

            migrationBuilder.DropColumn(
                name: "Simulated",
                table: "Refunds");

            migrationBuilder.AddPrimaryKey(
                name: "PK_Shipments",
                table: "Shipments",
                column: "OrderId");

            migrationBuilder.CreateIndex(
                name: "IX_ReturnItems_OrderItemId",
                table: "ReturnItems",
                column: "OrderItemId");

            migrationBuilder.CreateIndex(
                name: "IX_Refunds_PaymentAttemptId",
                table: "Refunds",
                column: "PaymentAttemptId");

            migrationBuilder.CreateIndex(
                name: "IX_Refunds_SellerOrderId",
                table: "Refunds",
                column: "SellerOrderId");

            migrationBuilder.AddCheckConstraint(
                name: "CK_Refunds_Amount",
                table: "Refunds",
                sql: "[AmountVnd] > 0");

            migrationBuilder.AddForeignKey(
                name: "FK_Refunds_PaymentAttempts_PaymentAttemptId",
                table: "Refunds",
                column: "PaymentAttemptId",
                principalTable: "PaymentAttempts",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_Refunds_ReturnRequests_ReturnId",
                table: "Refunds",
                column: "ReturnId",
                principalTable: "ReturnRequests",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_Refunds_SellerOrders_SellerOrderId",
                table: "Refunds",
                column: "SellerOrderId",
                principalTable: "SellerOrders",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_ReturnItems_OrderItems_OrderItemId",
                table: "ReturnItems",
                column: "OrderItemId",
                principalTable: "OrderItems",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_ReturnItems_ReturnRequests_ReturnId",
                table: "ReturnItems",
                column: "ReturnId",
                principalTable: "ReturnRequests",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_ReturnRequests_Orders_OrderId",
                table: "ReturnRequests",
                column: "OrderId",
                principalTable: "Orders",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_Shipments_Orders_OrderId",
                table: "Shipments",
                column: "OrderId",
                principalTable: "Orders",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }
    }
}
