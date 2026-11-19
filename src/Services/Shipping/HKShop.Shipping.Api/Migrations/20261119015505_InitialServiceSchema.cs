using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace HKShop.Shipping.Api.Migrations
{
    /// <inheritdoc />
    public partial class InitialServiceSchema : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "AuditEntries",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    SellerId = table.Column<long>(type: "bigint", nullable: true),
                    ActorId = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: true),
                    Action = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    Target = table.Column<string>(type: "nvarchar(150)", maxLength: 150, nullable: false),
                    Reason = table.Column<string>(type: "nvarchar(250)", maxLength: 250, nullable: false),
                    OccurredAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AuditEntries", x => x.Id);
                });

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
                name: "ShippingOrderSnapshots",
                columns: table => new
                {
                    SellerOrderId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    OrderId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    SellerId = table.Column<long>(type: "bigint", nullable: false),
                    SellerOrderNumber = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    SellerOrderStatus = table.Column<int>(type: "int", nullable: false),
                    TotalVnd = table.Column<decimal>(type: "decimal(18,0)", precision: 18, scale: 0, nullable: false),
                    ShippingVnd = table.Column<decimal>(type: "decimal(18,0)", precision: 18, scale: 0, nullable: false),
                    PaymentMethod = table.Column<int>(type: "int", nullable: false),
                    PaymentStatus = table.Column<int>(type: "int", nullable: false),
                    Recipient = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    Phone = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    ShippingAddress = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: false),
                    PendingPackedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    CancelledBeforePlacement = table.Column<bool>(type: "bit", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ShippingOrderSnapshots", x => x.SellerOrderId);
                });

            migrationBuilder.CreateTable(
                name: "ShippingSellerLocations",
                columns: table => new
                {
                    LocationId = table.Column<long>(type: "bigint", nullable: false),
                    SellerId = table.Column<long>(type: "bigint", nullable: false),
                    Name = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    PickupAddress = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: true),
                    Latitude = table.Column<double>(type: "float", nullable: true),
                    Longitude = table.Column<double>(type: "float", nullable: true),
                    Active = table.Column<bool>(type: "bit", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ShippingSellerLocations", x => x.LocationId);
                });

            migrationBuilder.CreateTable(
                name: "ShippingSellerMemberships",
                columns: table => new
                {
                    SellerId = table.Column<long>(type: "bigint", nullable: false),
                    UserId = table.Column<string>(type: "nvarchar(450)", nullable: false),
                    Role = table.Column<int>(type: "int", nullable: false),
                    Status = table.Column<int>(type: "int", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ShippingSellerMemberships", x => new { x.SellerId, x.UserId });
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
                });

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
                });

            migrationBuilder.CreateIndex(
                name: "IX_AuditEntries_OccurredAt",
                table: "AuditEntries",
                column: "OccurredAt");

            migrationBuilder.CreateIndex(
                name: "IX_AuditEntries_SellerId_OccurredAt",
                table: "AuditEntries",
                columns: new[] { "SellerId", "OccurredAt" });

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
                name: "IX_ShippingOrderSnapshots_OrderId",
                table: "ShippingOrderSnapshots",
                column: "OrderId");

            migrationBuilder.CreateIndex(
                name: "IX_ShippingSellerLocations_SellerId_Active",
                table: "ShippingSellerLocations",
                columns: new[] { "SellerId", "Active" });

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
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AuditEntries");

            migrationBuilder.DropTable(
                name: "ConsumedIntegrationEvents");

            migrationBuilder.DropTable(
                name: "IntegrationOutbox");

            migrationBuilder.DropTable(
                name: "ShippingOrderSnapshots");

            migrationBuilder.DropTable(
                name: "ShippingSellerLocations");

            migrationBuilder.DropTable(
                name: "ShippingSellerMemberships");

            migrationBuilder.DropTable(
                name: "TransportAssignments");

            migrationBuilder.DropTable(
                name: "TransportBankReceipts");

            migrationBuilder.DropTable(
                name: "TransportParcels");

            migrationBuilder.DropTable(
                name: "TransportShippers");
        }
    }
}
