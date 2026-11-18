using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace HKShop.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class MarketplaceCheckoutAndPayments : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"
DROP TRIGGER IF EXISTS dbo.TR_StockBalances_FirstPartyInventory;
DROP TRIGGER IF EXISTS dbo.TR_StockMovements_FirstPartyHistory;
");

            migrationBuilder.DropForeignKey(
                name: "FK_OrderEvents_Orders_OrderId",
                table: "OrderEvents");

            migrationBuilder.DropForeignKey(
                name: "FK_OrderItems_Orders_OrderId",
                table: "OrderItems");

            migrationBuilder.DropForeignKey(
                name: "FK_PaymentEvents_Payments_OrderId",
                table: "PaymentEvents");

            migrationBuilder.DropForeignKey(
                name: "FK_Payments_Orders_OrderId",
                table: "Payments");

            migrationBuilder.DropForeignKey(
                name: "FK_Refunds_Payments_OrderId",
                table: "Refunds");

            migrationBuilder.DropForeignKey(
                name: "FK_Shipments_Orders_OrderId",
                table: "Shipments");

            migrationBuilder.DropIndex(
                name: "IX_StockReservations_OrderId_SkuId",
                table: "StockReservations");

            migrationBuilder.RenameColumn(
                name: "RecordedAt",
                table: "PaymentEvents",
                newName: "ReceivedAt");

            migrationBuilder.RenameColumn(
                name: "OutcomeKey",
                table: "PaymentEvents",
                newName: "EventKey");

            migrationBuilder.RenameIndex(
                name: "IX_PaymentEvents_OutcomeKey",
                table: "PaymentEvents",
                newName: "IX_PaymentEvents_EventKey");

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "ExpiresAt",
                table: "StockReservations",
                type: "datetimeoffset",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "InventoryBalanceId",
                table: "StockReservations",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddColumn<long>(
                name: "OfferId",
                table: "StockReservations",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddColumn<long>(
                name: "OrderItemId",
                table: "StockReservations",
                type: "bigint",
                nullable: true);

            migrationBuilder.AlterColumn<string>(
                name: "BusinessKey",
                table: "Refunds",
                type: "nvarchar(120)",
                maxLength: 120,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "nvarchar(80)",
                oldMaxLength: 80);

            migrationBuilder.AddColumn<Guid>(
                name: "PaymentAttemptId",
                table: "Refunds",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "SellerOrderId",
                table: "Refunds",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "EventType",
                table: "PaymentEvents",
                type: "nvarchar(60)",
                maxLength: 60,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<Guid>(
                name: "PaymentAttemptId",
                table: "PaymentEvents",
                type: "uniqueidentifier",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            migrationBuilder.AlterColumn<long>(
                name: "SellerId",
                table: "Orders",
                type: "bigint",
                nullable: true,
                oldClrType: typeof(long),
                oldType: "bigint",
                oldDefaultValue: 1L);

            migrationBuilder.AddColumn<int>(
                name: "PaymentStatus",
                table: "Orders",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "CommissionBps",
                table: "OrderItems",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<decimal>(
                name: "CommissionVnd",
                table: "OrderItems",
                type: "decimal(18,0)",
                precision: 18,
                scale: 0,
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<long>(
                name: "OfferId",
                table: "OrderItems",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddColumn<decimal>(
                name: "PlatformDiscountVnd",
                table: "OrderItems",
                type: "decimal(18,0)",
                precision: 18,
                scale: 0,
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<string>(
                name: "ProductAndVariantSnapshot",
                table: "OrderItems",
                type: "nvarchar(300)",
                maxLength: 300,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<decimal>(
                name: "SellerDiscountVnd",
                table: "OrderItems",
                type: "decimal(18,0)",
                precision: 18,
                scale: 0,
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<long>(
                name: "SellerId",
                table: "OrderItems",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddColumn<Guid>(
                name: "SellerOrderId",
                table: "OrderItems",
                type: "uniqueidentifier",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            migrationBuilder.AddColumn<string>(
                name: "SellerSkuCode",
                table: "OrderItems",
                type: "nvarchar(64)",
                maxLength: 64,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddUniqueConstraint(
                name: "AK_OrderItems_Id_OfferId",
                table: "OrderItems",
                columns: new[] { "Id", "OfferId" });

            migrationBuilder.AddUniqueConstraint(
                name: "AK_InventoryBalances_Id_OfferId",
                table: "InventoryBalances",
                columns: new[] { "Id", "OfferId" });

            migrationBuilder.CreateTable(
                name: "ConsumedIntegrationEvents",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    ConsumerName = table.Column<string>(type: "nvarchar(80)", maxLength: 80, nullable: false),
                    EventKey = table.Column<string>(type: "nvarchar(160)", maxLength: 160, nullable: false),
                    ConsumedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ConsumedIntegrationEvents", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "IntegrationOutbox",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    EventId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    AggregateType = table.Column<string>(type: "nvarchar(60)", maxLength: 60, nullable: false),
                    AggregateId = table.Column<string>(type: "nvarchar(80)", maxLength: 80, nullable: false),
                    EventType = table.Column<string>(type: "nvarchar(80)", maxLength: 80, nullable: false),
                    PayloadJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    OccurredAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    PublishedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    IdempotencyKey = table.Column<string>(type: "nvarchar(160)", maxLength: 160, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_IntegrationOutbox", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "PaymentAttempts",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    OrderId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    AttemptNumber = table.Column<int>(type: "int", nullable: false),
                    Method = table.Column<int>(type: "int", nullable: false),
                    Status = table.Column<int>(type: "int", nullable: false),
                    AmountVnd = table.Column<decimal>(type: "decimal(18,0)", precision: 18, scale: 0, nullable: false),
                    CapturedAmountVnd = table.Column<decimal>(type: "decimal(18,0)", precision: 18, scale: 0, nullable: false),
                    ProviderInvoice = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: true),
                    ProviderTransaction = table.Column<string>(type: "nvarchar(120)", maxLength: 120, nullable: true),
                    AppliedToOrder = table.Column<bool>(type: "bit", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    PaidAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    NextQueryAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    Issue = table.Column<string>(type: "nvarchar(250)", maxLength: 250, nullable: false),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PaymentAttempts", x => x.Id);
                    table.CheckConstraint("CK_PaymentAttempts_Amounts", "[AttemptNumber] > 0 AND [AmountVnd] >= 0 AND [CapturedAmountVnd] >= 0");
                    table.ForeignKey(
                        name: "FK_PaymentAttempts_Orders_OrderId",
                        column: x => x.OrderId,
                        principalTable: "Orders",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "SellerOrders",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    OrderId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    SellerId = table.Column<long>(type: "bigint", nullable: false),
                    Number = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    Status = table.Column<int>(type: "int", nullable: false),
                    SubtotalVnd = table.Column<decimal>(type: "decimal(18,0)", precision: 18, scale: 0, nullable: false),
                    SellerDiscountVnd = table.Column<decimal>(type: "decimal(18,0)", precision: 18, scale: 0, nullable: false),
                    PlatformDiscountVnd = table.Column<decimal>(type: "decimal(18,0)", precision: 18, scale: 0, nullable: false),
                    ShippingVnd = table.Column<decimal>(type: "decimal(18,0)", precision: 18, scale: 0, nullable: false),
                    TotalVnd = table.Column<decimal>(type: "decimal(18,0)", precision: 18, scale: 0, nullable: false),
                    CommissionVnd = table.Column<decimal>(type: "decimal(18,0)", precision: 18, scale: 0, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    ConfirmedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    CancelledAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SellerOrders", x => x.Id);
                    table.UniqueConstraint("AK_SellerOrders_Id_OrderId_SellerId", x => new { x.Id, x.OrderId, x.SellerId });
                    table.UniqueConstraint("AK_SellerOrders_Id_SellerId", x => new { x.Id, x.SellerId });
                    table.CheckConstraint("CK_SellerOrders_Amounts", "[SubtotalVnd] >= 0 AND [SellerDiscountVnd] >= 0 AND [PlatformDiscountVnd] >= 0 AND [ShippingVnd] >= 0 AND [TotalVnd] >= 0 AND [CommissionVnd] >= 0");
                    table.ForeignKey(
                        name: "FK_SellerOrders_Orders_OrderId",
                        column: x => x.OrderId,
                        principalTable: "Orders",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_SellerOrders_Sellers_SellerId",
                        column: x => x.SellerId,
                        principalTable: "Sellers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_StockReservations_InventoryBalanceId_OfferId",
                table: "StockReservations",
                columns: new[] { "InventoryBalanceId", "OfferId" });

            migrationBuilder.CreateIndex(
                name: "IX_StockReservations_OrderId_SkuId",
                table: "StockReservations",
                columns: new[] { "OrderId", "SkuId" });

            migrationBuilder.CreateIndex(
                name: "IX_StockReservations_OrderItemId_InventoryBalanceId",
                table: "StockReservations",
                columns: new[] { "OrderItemId", "InventoryBalanceId" },
                unique: true,
                filter: "[OrderItemId] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_StockReservations_OrderItemId_OfferId",
                table: "StockReservations",
                columns: new[] { "OrderItemId", "OfferId" });

            migrationBuilder.CreateIndex(
                name: "IX_StockReservations_Status_ExpiresAt",
                table: "StockReservations",
                columns: new[] { "Status", "ExpiresAt" });

            migrationBuilder.AddCheckConstraint(
                name: "CK_StockReservations_Quantity",
                table: "StockReservations",
                sql: "[Quantity] > 0");

            migrationBuilder.CreateIndex(
                name: "IX_Refunds_PaymentAttemptId",
                table: "Refunds",
                column: "PaymentAttemptId");

            migrationBuilder.CreateIndex(
                name: "IX_Refunds_SellerOrderId",
                table: "Refunds",
                column: "SellerOrderId");

            migrationBuilder.CreateIndex(
                name: "IX_Refunds_Status_RequestedAt",
                table: "Refunds",
                columns: new[] { "Status", "RequestedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_PaymentEvents_PaymentAttemptId_ReceivedAt",
                table: "PaymentEvents",
                columns: new[] { "PaymentAttemptId", "ReceivedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_Orders_PaymentStatus_PaymentDeadline",
                table: "Orders",
                columns: new[] { "PaymentStatus", "PaymentDeadline" });

            migrationBuilder.CreateIndex(
                name: "IX_OrderItems_OfferId_SellerId",
                table: "OrderItems",
                columns: new[] { "OfferId", "SellerId" });

            migrationBuilder.CreateIndex(
                name: "IX_OrderItems_OfferId_SkuId",
                table: "OrderItems",
                columns: new[] { "OfferId", "SkuId" });

            migrationBuilder.CreateIndex(
                name: "IX_OrderItems_SellerOrderId_OfferId",
                table: "OrderItems",
                columns: new[] { "SellerOrderId", "OfferId" });

            migrationBuilder.CreateIndex(
                name: "IX_OrderItems_SellerOrderId_OrderId_SellerId",
                table: "OrderItems",
                columns: new[] { "SellerOrderId", "OrderId", "SellerId" });

            migrationBuilder.AddCheckConstraint(
                name: "CK_OrderItems_Amounts",
                table: "OrderItems",
                sql: "[CommissionBps] BETWEEN 0 AND 10000 AND [Quantity] > 0 AND [UnitPriceVnd] >= 0 AND [DiscountVnd] >= 0 AND [SellerDiscountVnd] >= 0 AND [PlatformDiscountVnd] >= 0 AND [CommissionVnd] >= 0");

            migrationBuilder.CreateIndex(
                name: "IX_ConsumedIntegrationEvents_ConsumerName_EventKey",
                table: "ConsumedIntegrationEvents",
                columns: new[] { "ConsumerName", "EventKey" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_IntegrationOutbox_EventId",
                table: "IntegrationOutbox",
                column: "EventId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_IntegrationOutbox_IdempotencyKey",
                table: "IntegrationOutbox",
                column: "IdempotencyKey",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_IntegrationOutbox_PublishedAt_OccurredAt",
                table: "IntegrationOutbox",
                columns: new[] { "PublishedAt", "OccurredAt" });

            migrationBuilder.CreateIndex(
                name: "IX_PaymentAttempts_OrderId_AppliedToOrder",
                table: "PaymentAttempts",
                column: "OrderId",
                unique: true,
                filter: "[AppliedToOrder] = 1");

            migrationBuilder.CreateIndex(
                name: "IX_PaymentAttempts_OrderId_AttemptNumber",
                table: "PaymentAttempts",
                columns: new[] { "OrderId", "AttemptNumber" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_PaymentAttempts_PaidAt",
                table: "PaymentAttempts",
                column: "PaidAt");

            migrationBuilder.CreateIndex(
                name: "IX_PaymentAttempts_ProviderInvoice",
                table: "PaymentAttempts",
                column: "ProviderInvoice",
                unique: true,
                filter: "[ProviderInvoice] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_PaymentAttempts_ProviderTransaction",
                table: "PaymentAttempts",
                column: "ProviderTransaction",
                unique: true,
                filter: "[ProviderTransaction] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_PaymentAttempts_Status_NextQueryAt",
                table: "PaymentAttempts",
                columns: new[] { "Status", "NextQueryAt" });

            migrationBuilder.CreateIndex(
                name: "IX_SellerOrders_Number",
                table: "SellerOrders",
                column: "Number",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_SellerOrders_OrderId_SellerId",
                table: "SellerOrders",
                columns: new[] { "OrderId", "SellerId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_SellerOrders_SellerId_Status_CreatedAt",
                table: "SellerOrders",
                columns: new[] { "SellerId", "Status", "CreatedAt" });

            migrationBuilder.Sql(@"
UPDATE o
SET o.PaymentStatus = p.Status
FROM dbo.Orders o
INNER JOIN dbo.Payments p ON p.OrderId = o.Id;

INSERT INTO dbo.SellerOrders (
    Id, OrderId, SellerId, Number, Status,
    SubtotalVnd, SellerDiscountVnd, PlatformDiscountVnd, ShippingVnd, TotalVnd, CommissionVnd,
    CreatedAt, ConfirmedAt, CancelledAt)
SELECT
    NEWID(),
    o.Id,
    COALESCE(o.SellerId, 1),
    LEFT(o.Number + N'-S1', 50),
    o.Status,
    ISNULL(agg.SubtotalVnd, CASE WHEN o.TotalVnd >= o.ShippingVnd THEN o.TotalVnd - o.ShippingVnd ELSE o.TotalVnd END),
    0,
    ISNULL(agg.DiscountVnd, 0),
    o.ShippingVnd,
    o.TotalVnd,
    0,
    o.CreatedAt,
    CASE WHEN o.Status IN (1, 2, 3, 4, 7) THEN o.CreatedAt ELSE NULL END,
    CASE WHEN o.Status IN (5, 6) THEN o.CreatedAt ELSE NULL END
FROM dbo.Orders o
OUTER APPLY (
    SELECT
        SUM(oi.UnitPriceVnd * oi.Quantity) AS SubtotalVnd,
        SUM(oi.DiscountVnd) AS DiscountVnd
    FROM dbo.OrderItems oi
    WHERE oi.OrderId = o.Id
) agg
WHERE NOT EXISTS (
    SELECT 1 FROM dbo.SellerOrders so
    WHERE so.OrderId = o.Id AND so.SellerId = COALESCE(o.SellerId, 1)
);

UPDATE oi
SET
    oi.SellerOrderId = so.Id,
    oi.SellerId = so.SellerId,
    oi.OfferId = offe.Id,
    oi.SellerSkuCode = CASE WHEN offe.SellerSkuCode <> N'' THEN offe.SellerSkuCode ELSE oi.SkuCode END,
    oi.ProductAndVariantSnapshot = LEFT(LTRIM(RTRIM(
        pr.Name
        + CASE WHEN s.Size <> N'' THEN N' / ' + s.Size ELSE N'' END
        + CASE WHEN s.Color <> N'' THEN N' / ' + s.Color ELSE N'' END)), 300),
    oi.SellerDiscountVnd = 0,
    oi.PlatformDiscountVnd = oi.DiscountVnd,
    oi.CommissionBps = 0,
    oi.CommissionVnd = 0
FROM dbo.OrderItems oi
INNER JOIN dbo.Orders o ON o.Id = oi.OrderId
INNER JOIN dbo.SellerOrders so ON so.OrderId = o.Id AND so.SellerId = COALESCE(o.SellerId, 1)
INNER JOIN dbo.SellerOffers offe ON offe.SellerId = so.SellerId AND offe.SkuId = oi.SkuId
INNER JOIN dbo.Skus s ON s.Id = oi.SkuId
INNER JOIN dbo.Products pr ON pr.Id = s.ProductId;

UPDATE sr
SET
    sr.OrderItemId = itemMatch.OrderItemId,
    sr.OfferId = offe.Id,
    sr.InventoryBalanceId = ib.Id,
    sr.ExpiresAt = o.PaymentDeadline
FROM dbo.StockReservations sr
INNER JOIN dbo.Orders o ON o.Id = sr.OrderId
OUTER APPLY (
    SELECT TOP (1) oi.Id AS OrderItemId, oi.OfferId
    FROM dbo.OrderItems oi
    WHERE oi.OrderId = sr.OrderId AND oi.SkuId = sr.SkuId
    ORDER BY oi.Id
) itemMatch
INNER JOIN dbo.SellerOffers offe
    ON (itemMatch.OfferId IS NOT NULL AND offe.Id = itemMatch.OfferId)
    OR (itemMatch.OfferId IS NULL AND offe.SellerId = COALESCE(o.SellerId, 1) AND offe.SkuId = sr.SkuId)
CROSS APPLY (
    SELECT TOP (1) ib0.Id
    FROM dbo.InventoryBalances ib0
    WHERE ib0.OfferId = offe.Id AND ib0.SellerId = offe.SellerId
    ORDER BY ib0.Id
) ib;

INSERT INTO dbo.PaymentAttempts (
    Id, OrderId, AttemptNumber, Method, Status,
    AmountVnd, CapturedAmountVnd, ProviderInvoice, ProviderTransaction,
    AppliedToOrder, CreatedAt, PaidAt, NextQueryAt, Issue)
SELECT
    NEWID(),
    p.OrderId,
    1,
    p.Method,
    p.Status,
    p.AmountVnd,
    CASE WHEN p.Status = 2 THEN p.AmountVnd ELSE 0 END,
    CASE WHEN p.Method = 1 THEN o.Number ELSE NULL END,
    p.ProviderTransaction,
    CASE WHEN p.Status = 2 THEN 1 ELSE 0 END,
    o.CreatedAt,
    p.PaidAt,
    p.NextQueryAt,
    p.Issue
FROM dbo.Payments p
INNER JOIN dbo.Orders o ON o.Id = p.OrderId
WHERE NOT EXISTS (SELECT 1 FROM dbo.PaymentAttempts pa WHERE pa.OrderId = p.OrderId AND pa.AttemptNumber = 1);

UPDATE pe
SET
    pe.PaymentAttemptId = pa.Id,
    pe.EventType = N'Captured'
FROM dbo.PaymentEvents pe
INNER JOIN dbo.PaymentAttempts pa ON pa.OrderId = pe.OrderId AND pa.AttemptNumber = 1;

UPDATE r
SET
    r.PaymentAttemptId = CASE WHEN pa.AppliedToOrder = 1 OR p.Method = 1 THEN pa.Id ELSE NULL END,
    r.SellerOrderId = so.Id
FROM dbo.Refunds r
INNER JOIN dbo.Orders o ON o.Id = r.OrderId
LEFT JOIN dbo.Payments p ON p.OrderId = r.OrderId
LEFT JOIN dbo.PaymentAttempts pa ON pa.OrderId = r.OrderId AND pa.AttemptNumber = 1
LEFT JOIN dbo.SellerOrders so ON so.OrderId = r.OrderId AND so.SellerId = COALESCE(o.SellerId, 1);
");

            migrationBuilder.AddForeignKey(
                name: "FK_OrderEvents_Orders_OrderId",
                table: "OrderEvents",
                column: "OrderId",
                principalTable: "Orders",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_OrderItems_Orders_OrderId",
                table: "OrderItems",
                column: "OrderId",
                principalTable: "Orders",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_OrderItems_SellerOffers_OfferId_SellerId",
                table: "OrderItems",
                columns: new[] { "OfferId", "SellerId" },
                principalTable: "SellerOffers",
                principalColumns: new[] { "Id", "SellerId" },
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_OrderItems_SellerOffers_OfferId_SkuId",
                table: "OrderItems",
                columns: new[] { "OfferId", "SkuId" },
                principalTable: "SellerOffers",
                principalColumns: new[] { "Id", "SkuId" },
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_OrderItems_SellerOrders_SellerOrderId_OrderId_SellerId",
                table: "OrderItems",
                columns: new[] { "SellerOrderId", "OrderId", "SellerId" },
                principalTable: "SellerOrders",
                principalColumns: new[] { "Id", "OrderId", "SellerId" },
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_PaymentEvents_Orders_OrderId",
                table: "PaymentEvents",
                column: "OrderId",
                principalTable: "Orders",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_PaymentEvents_PaymentAttempts_PaymentAttemptId",
                table: "PaymentEvents",
                column: "PaymentAttemptId",
                principalTable: "PaymentAttempts",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_Payments_Orders_OrderId",
                table: "Payments",
                column: "OrderId",
                principalTable: "Orders",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_Refunds_Orders_OrderId",
                table: "Refunds",
                column: "OrderId",
                principalTable: "Orders",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_Refunds_PaymentAttempts_PaymentAttemptId",
                table: "Refunds",
                column: "PaymentAttemptId",
                principalTable: "PaymentAttempts",
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
                name: "FK_Shipments_Orders_OrderId",
                table: "Shipments",
                column: "OrderId",
                principalTable: "Orders",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_StockReservations_InventoryBalances_InventoryBalanceId_OfferId",
                table: "StockReservations",
                columns: new[] { "InventoryBalanceId", "OfferId" },
                principalTable: "InventoryBalances",
                principalColumns: new[] { "Id", "OfferId" },
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_StockReservations_OrderItems_OrderItemId_OfferId",
                table: "StockReservations",
                columns: new[] { "OrderItemId", "OfferId" },
                principalTable: "OrderItems",
                principalColumns: new[] { "Id", "OfferId" },
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_OrderEvents_Orders_OrderId",
                table: "OrderEvents");

            migrationBuilder.DropForeignKey(
                name: "FK_OrderItems_Orders_OrderId",
                table: "OrderItems");

            migrationBuilder.DropForeignKey(
                name: "FK_OrderItems_SellerOffers_OfferId_SellerId",
                table: "OrderItems");

            migrationBuilder.DropForeignKey(
                name: "FK_OrderItems_SellerOffers_OfferId_SkuId",
                table: "OrderItems");

            migrationBuilder.DropForeignKey(
                name: "FK_OrderItems_SellerOrders_SellerOrderId_OrderId_SellerId",
                table: "OrderItems");

            migrationBuilder.DropForeignKey(
                name: "FK_PaymentEvents_Orders_OrderId",
                table: "PaymentEvents");

            migrationBuilder.DropForeignKey(
                name: "FK_PaymentEvents_PaymentAttempts_PaymentAttemptId",
                table: "PaymentEvents");

            migrationBuilder.DropForeignKey(
                name: "FK_Payments_Orders_OrderId",
                table: "Payments");

            migrationBuilder.DropForeignKey(
                name: "FK_Refunds_Orders_OrderId",
                table: "Refunds");

            migrationBuilder.DropForeignKey(
                name: "FK_Refunds_PaymentAttempts_PaymentAttemptId",
                table: "Refunds");

            migrationBuilder.DropForeignKey(
                name: "FK_Refunds_SellerOrders_SellerOrderId",
                table: "Refunds");

            migrationBuilder.DropForeignKey(
                name: "FK_Shipments_Orders_OrderId",
                table: "Shipments");

            migrationBuilder.DropForeignKey(
                name: "FK_StockReservations_InventoryBalances_InventoryBalanceId_OfferId",
                table: "StockReservations");

            migrationBuilder.DropForeignKey(
                name: "FK_StockReservations_OrderItems_OrderItemId_OfferId",
                table: "StockReservations");

            migrationBuilder.DropTable(
                name: "ConsumedIntegrationEvents");

            migrationBuilder.DropTable(
                name: "IntegrationOutbox");

            migrationBuilder.DropTable(
                name: "PaymentAttempts");

            migrationBuilder.DropTable(
                name: "SellerOrders");

            migrationBuilder.DropIndex(
                name: "IX_StockReservations_InventoryBalanceId_OfferId",
                table: "StockReservations");

            migrationBuilder.DropIndex(
                name: "IX_StockReservations_OrderId_SkuId",
                table: "StockReservations");

            migrationBuilder.DropIndex(
                name: "IX_StockReservations_OrderItemId_InventoryBalanceId",
                table: "StockReservations");

            migrationBuilder.DropIndex(
                name: "IX_StockReservations_OrderItemId_OfferId",
                table: "StockReservations");

            migrationBuilder.DropIndex(
                name: "IX_StockReservations_Status_ExpiresAt",
                table: "StockReservations");

            migrationBuilder.DropCheckConstraint(
                name: "CK_StockReservations_Quantity",
                table: "StockReservations");

            migrationBuilder.DropIndex(
                name: "IX_Refunds_PaymentAttemptId",
                table: "Refunds");

            migrationBuilder.DropIndex(
                name: "IX_Refunds_SellerOrderId",
                table: "Refunds");

            migrationBuilder.DropIndex(
                name: "IX_Refunds_Status_RequestedAt",
                table: "Refunds");

            migrationBuilder.DropIndex(
                name: "IX_PaymentEvents_PaymentAttemptId_ReceivedAt",
                table: "PaymentEvents");

            migrationBuilder.DropIndex(
                name: "IX_Orders_PaymentStatus_PaymentDeadline",
                table: "Orders");

            migrationBuilder.DropUniqueConstraint(
                name: "AK_OrderItems_Id_OfferId",
                table: "OrderItems");

            migrationBuilder.DropIndex(
                name: "IX_OrderItems_OfferId_SellerId",
                table: "OrderItems");

            migrationBuilder.DropIndex(
                name: "IX_OrderItems_OfferId_SkuId",
                table: "OrderItems");

            migrationBuilder.DropIndex(
                name: "IX_OrderItems_SellerOrderId_OfferId",
                table: "OrderItems");

            migrationBuilder.DropIndex(
                name: "IX_OrderItems_SellerOrderId_OrderId_SellerId",
                table: "OrderItems");

            migrationBuilder.DropCheckConstraint(
                name: "CK_OrderItems_Amounts",
                table: "OrderItems");

            migrationBuilder.DropUniqueConstraint(
                name: "AK_InventoryBalances_Id_OfferId",
                table: "InventoryBalances");

            migrationBuilder.DropColumn(
                name: "ExpiresAt",
                table: "StockReservations");

            migrationBuilder.DropColumn(
                name: "InventoryBalanceId",
                table: "StockReservations");

            migrationBuilder.DropColumn(
                name: "OfferId",
                table: "StockReservations");

            migrationBuilder.DropColumn(
                name: "OrderItemId",
                table: "StockReservations");

            migrationBuilder.DropColumn(
                name: "PaymentAttemptId",
                table: "Refunds");

            migrationBuilder.DropColumn(
                name: "SellerOrderId",
                table: "Refunds");

            migrationBuilder.DropColumn(
                name: "EventType",
                table: "PaymentEvents");

            migrationBuilder.DropColumn(
                name: "PaymentAttemptId",
                table: "PaymentEvents");

            migrationBuilder.DropColumn(
                name: "PaymentStatus",
                table: "Orders");

            migrationBuilder.DropColumn(
                name: "CommissionBps",
                table: "OrderItems");

            migrationBuilder.DropColumn(
                name: "CommissionVnd",
                table: "OrderItems");

            migrationBuilder.DropColumn(
                name: "OfferId",
                table: "OrderItems");

            migrationBuilder.DropColumn(
                name: "PlatformDiscountVnd",
                table: "OrderItems");

            migrationBuilder.DropColumn(
                name: "ProductAndVariantSnapshot",
                table: "OrderItems");

            migrationBuilder.DropColumn(
                name: "SellerDiscountVnd",
                table: "OrderItems");

            migrationBuilder.DropColumn(
                name: "SellerId",
                table: "OrderItems");

            migrationBuilder.DropColumn(
                name: "SellerOrderId",
                table: "OrderItems");

            migrationBuilder.DropColumn(
                name: "SellerSkuCode",
                table: "OrderItems");

            migrationBuilder.RenameColumn(
                name: "ReceivedAt",
                table: "PaymentEvents",
                newName: "RecordedAt");

            migrationBuilder.RenameColumn(
                name: "EventKey",
                table: "PaymentEvents",
                newName: "OutcomeKey");

            migrationBuilder.RenameIndex(
                name: "IX_PaymentEvents_EventKey",
                table: "PaymentEvents",
                newName: "IX_PaymentEvents_OutcomeKey");

            migrationBuilder.AlterColumn<string>(
                name: "BusinessKey",
                table: "Refunds",
                type: "nvarchar(80)",
                maxLength: 80,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "nvarchar(120)",
                oldMaxLength: 120);

            migrationBuilder.Sql("UPDATE dbo.Orders SET SellerId = 1 WHERE SellerId IS NULL;");

            migrationBuilder.AlterColumn<long>(
                name: "SellerId",
                table: "Orders",
                type: "bigint",
                nullable: false,
                defaultValue: 1L,
                oldClrType: typeof(long),
                oldType: "bigint",
                oldNullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_StockReservations_OrderId_SkuId",
                table: "StockReservations",
                columns: new[] { "OrderId", "SkuId" },
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "FK_OrderEvents_Orders_OrderId",
                table: "OrderEvents",
                column: "OrderId",
                principalTable: "Orders",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_OrderItems_Orders_OrderId",
                table: "OrderItems",
                column: "OrderId",
                principalTable: "Orders",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_PaymentEvents_Payments_OrderId",
                table: "PaymentEvents",
                column: "OrderId",
                principalTable: "Payments",
                principalColumn: "OrderId",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_Payments_Orders_OrderId",
                table: "Payments",
                column: "OrderId",
                principalTable: "Orders",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_Refunds_Payments_OrderId",
                table: "Refunds",
                column: "OrderId",
                principalTable: "Payments",
                principalColumn: "OrderId",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_Shipments_Orders_OrderId",
                table: "Shipments",
                column: "OrderId",
                principalTable: "Orders",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);
        }
    }
}
