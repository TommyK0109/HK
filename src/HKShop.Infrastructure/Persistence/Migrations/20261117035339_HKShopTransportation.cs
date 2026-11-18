using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace HKShop.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class HKShopTransportation : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint("CK_LedgerAccounts_Scope", "LedgerAccounts");
            migrationBuilder.AddCheckConstraint("CK_LedgerAccounts_Scope", "LedgerAccounts",
                "([Type] BETWEEN 6 AND 8 AND [SellerId] IS NOT NULL) OR (([Type] BETWEEN 0 AND 5 OR [Type] BETWEEN 9 AND 11) AND [SellerId] IS NULL)");

            migrationBuilder.DropIndex(
                name: "IX_PaymentAttempts_OrderId_AppliedToOrder",
                table: "PaymentAttempts");

            migrationBuilder.AddColumn<double>(
                name: "Latitude",
                table: "SellerLocations",
                type: "float",
                nullable: true);

            migrationBuilder.AddColumn<double>(
                name: "Longitude",
                table: "SellerLocations",
                type: "float",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PickupAddress",
                table: "SellerLocations",
                type: "nvarchar(300)",
                maxLength: 300,
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "SellerOrderId",
                table: "PaymentAttempts",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.AddColumn<double>(
                name: "Latitude",
                table: "CustomerAddresses",
                type: "float",
                nullable: true);

            migrationBuilder.AddColumn<double>(
                name: "Longitude",
                table: "CustomerAddresses",
                type: "float",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "TransportShippers",
                columns: table => new
                {
                    UserId = table.Column<string>(type: "nvarchar(450)", nullable: false),
                    Email = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                    Active = table.Column<bool>(type: "bit", nullable: false),
                    OnDuty = table.Column<bool>(type: "bit", nullable: false),
                    Latitude = table.Column<double>(type: "float", nullable: true),
                    Longitude = table.Column<double>(type: "float", nullable: true),
                    LocatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TransportShippers", x => x.UserId);
                    table.ForeignKey(
                        name: "FK_TransportShippers_AspNetUsers_UserId",
                        column: x => x.UserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "TransportParcels",
                columns: table => new
                {
                    SellerOrderId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Stage = table.Column<int>(type: "int", nullable: false),
                    LocationId = table.Column<long>(type: "bigint", nullable: false),
                    PickupAddress = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: false),
                    PickupLatitude = table.Column<double>(type: "float", nullable: false),
                    PickupLongitude = table.Column<double>(type: "float", nullable: false),
                    DestinationLatitude = table.Column<double>(type: "float", nullable: false),
                    DestinationLongitude = table.Column<double>(type: "float", nullable: false),
                    DistanceMeters = table.Column<long>(type: "bigint", nullable: false),
                    DeliveryCode = table.Column<string>(type: "nvarchar(6)", maxLength: 6, nullable: false),
                    PackedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    PickupDueAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    RequestedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    NextDispatchAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    ShipperId = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: true),
                    AcknowledgeBy = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    AcknowledgedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    PickedUpAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    DoorPayment = table.Column<int>(type: "int", nullable: true),
                    FundsVerifiedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    CashCollectedVnd = table.Column<decimal>(type: "decimal(18,0)", precision: 18, scale: 0, nullable: false),
                    ShipperFeeVnd = table.Column<decimal>(type: "decimal(18,0)", precision: 18, scale: 0, nullable: false),
                    ShipperPaidAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    ShipperPayoutReference = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    Issue = table.Column<string>(type: "nvarchar(250)", maxLength: 250, nullable: false),
                    FailedDeliveryCodes = table.Column<int>(type: "int", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TransportParcels", x => x.SellerOrderId);
                    table.ForeignKey(
                        name: "FK_TransportParcels_SellerLocations_LocationId",
                        column: x => x.LocationId,
                        principalTable: "SellerLocations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_TransportParcels_SellerOrders_SellerOrderId",
                        column: x => x.SellerOrderId,
                        principalTable: "SellerOrders",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_TransportParcels_TransportShippers_ShipperId",
                        column: x => x.ShipperId,
                        principalTable: "TransportShippers",
                        principalColumn: "UserId",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "TransportAssignments",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    SellerOrderId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ShipperId = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: false),
                    AssignedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    EndedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    Outcome = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TransportAssignments", x => x.Id);
                    table.ForeignKey(
                        name: "FK_TransportAssignments_TransportParcels_SellerOrderId",
                        column: x => x.SellerOrderId,
                        principalTable: "TransportParcels",
                        principalColumn: "SellerOrderId",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_TransportAssignments_TransportShippers_ShipperId",
                        column: x => x.ShipperId,
                        principalTable: "TransportShippers",
                        principalColumn: "UserId",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "TransportBankReceipts",
                columns: table => new
                {
                    TransactionId = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    Reference = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    AmountVnd = table.Column<decimal>(type: "decimal(18,0)", precision: 18, scale: 0, nullable: false),
                    ReceivedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    SellerOrderId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    Issue = table.Column<string>(type: "nvarchar(250)", maxLength: 250, nullable: false),
                    ResolvedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    Resolution = table.Column<string>(type: "nvarchar(250)", maxLength: 250, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TransportBankReceipts", x => x.TransactionId);
                    table.ForeignKey(
                        name: "FK_TransportBankReceipts_TransportParcels_SellerOrderId",
                        column: x => x.SellerOrderId,
                        principalTable: "TransportParcels",
                        principalColumn: "SellerOrderId",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.UpdateData(
                table: "SellerLocations",
                keyColumn: "Id",
                keyValue: 1L,
                columns: new[] { "Latitude", "Longitude", "PickupAddress" },
                values: new object[] { null, null, null });

            migrationBuilder.CreateIndex(
                name: "IX_PaymentAttempts_OrderId_AppliedToOrder",
                table: "PaymentAttempts",
                column: "OrderId",
                unique: true,
                filter: "[AppliedToOrder] = 1 AND [SellerOrderId] IS NULL");

            migrationBuilder.CreateIndex(
                name: "IX_PaymentAttempts_SellerOrderId",
                table: "PaymentAttempts",
                column: "SellerOrderId",
                unique: true,
                filter: "[AppliedToOrder] = 1 AND [SellerOrderId] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_PaymentAttempts_SellerOrderId_OrderId",
                table: "PaymentAttempts",
                columns: new[] { "SellerOrderId", "OrderId" });

            migrationBuilder.CreateIndex(
                name: "IX_TransportAssignments_ActiveParcel",
                table: "TransportAssignments",
                column: "SellerOrderId",
                unique: true,
                filter: "[EndedAt] IS NULL");

            migrationBuilder.CreateIndex(
                name: "IX_TransportAssignments_ActiveShipper",
                table: "TransportAssignments",
                column: "ShipperId",
                unique: true,
                filter: "[EndedAt] IS NULL");

            migrationBuilder.CreateIndex(
                name: "IX_TransportAssignments_SellerOrderId_ShipperId",
                table: "TransportAssignments",
                columns: new[] { "SellerOrderId", "ShipperId" });

            migrationBuilder.CreateIndex(
                name: "IX_TransportBankReceipts_ReceivedAt",
                table: "TransportBankReceipts",
                column: "ReceivedAt");

            migrationBuilder.CreateIndex(
                name: "IX_TransportBankReceipts_SellerOrderId",
                table: "TransportBankReceipts",
                column: "SellerOrderId");

            migrationBuilder.CreateIndex(
                name: "IX_TransportParcels_LocationId",
                table: "TransportParcels",
                column: "LocationId");

            migrationBuilder.CreateIndex(
                name: "IX_TransportParcels_ShipperId",
                table: "TransportParcels",
                column: "ShipperId");

            migrationBuilder.CreateIndex(
                name: "IX_TransportParcels_ShipperPayoutReference",
                table: "TransportParcels",
                column: "ShipperPayoutReference",
                unique: true,
                filter: "[ShipperPayoutReference] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_TransportParcels_Stage_AcknowledgeBy",
                table: "TransportParcels",
                columns: new[] { "Stage", "AcknowledgeBy" });

            migrationBuilder.CreateIndex(
                name: "IX_TransportParcels_Stage_NextDispatchAt",
                table: "TransportParcels",
                columns: new[] { "Stage", "NextDispatchAt" });

            migrationBuilder.CreateIndex(
                name: "IX_TransportParcels_Stage_PickupDueAt",
                table: "TransportParcels",
                columns: new[] { "Stage", "PickupDueAt" });

            migrationBuilder.CreateIndex(
                name: "IX_TransportShippers_Active_OnDuty_LocatedAt",
                table: "TransportShippers",
                columns: new[] { "Active", "OnDuty", "LocatedAt" });

            migrationBuilder.AddForeignKey(
                name: "FK_PaymentAttempts_SellerOrders_SellerOrderId_OrderId",
                table: "PaymentAttempts",
                columns: new[] { "SellerOrderId", "OrderId" },
                principalTable: "SellerOrders",
                principalColumns: new[] { "Id", "OrderId" },
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint("CK_LedgerAccounts_Scope", "LedgerAccounts");
            migrationBuilder.AddCheckConstraint("CK_LedgerAccounts_Scope", "LedgerAccounts",
                "([Type] BETWEEN 6 AND 8 AND [SellerId] IS NOT NULL) OR (([Type] BETWEEN 0 AND 5 OR [Type] = 9) AND [SellerId] IS NULL)");

            migrationBuilder.DropForeignKey(
                name: "FK_PaymentAttempts_SellerOrders_SellerOrderId_OrderId",
                table: "PaymentAttempts");

            migrationBuilder.DropTable(
                name: "TransportAssignments");

            migrationBuilder.DropTable(
                name: "TransportBankReceipts");

            migrationBuilder.DropTable(
                name: "TransportParcels");

            migrationBuilder.DropTable(
                name: "TransportShippers");

            migrationBuilder.DropIndex(
                name: "IX_PaymentAttempts_OrderId_AppliedToOrder",
                table: "PaymentAttempts");

            migrationBuilder.DropIndex(
                name: "IX_PaymentAttempts_SellerOrderId",
                table: "PaymentAttempts");

            migrationBuilder.DropIndex(
                name: "IX_PaymentAttempts_SellerOrderId_OrderId",
                table: "PaymentAttempts");

            migrationBuilder.DropColumn(
                name: "Latitude",
                table: "SellerLocations");

            migrationBuilder.DropColumn(
                name: "Longitude",
                table: "SellerLocations");

            migrationBuilder.DropColumn(
                name: "PickupAddress",
                table: "SellerLocations");

            migrationBuilder.DropColumn(
                name: "SellerOrderId",
                table: "PaymentAttempts");

            migrationBuilder.DropColumn(
                name: "Latitude",
                table: "CustomerAddresses");

            migrationBuilder.DropColumn(
                name: "Longitude",
                table: "CustomerAddresses");

            migrationBuilder.CreateIndex(
                name: "IX_PaymentAttempts_OrderId_AppliedToOrder",
                table: "PaymentAttempts",
                column: "OrderId",
                unique: true,
                filter: "[AppliedToOrder] = 1");
        }
    }
}
