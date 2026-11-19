using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace HKShop.Payments.Api.Migrations
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
                name: "LedgerAccounts",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    SellerId = table.Column<long>(type: "bigint", nullable: true),
                    Type = table.Column<int>(type: "int", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LedgerAccounts", x => x.Id);
                    table.CheckConstraint("CK_LedgerAccounts_Scope", "([Type] BETWEEN 6 AND 8 AND [SellerId] IS NOT NULL) OR (([Type] BETWEEN 0 AND 5 OR [Type] BETWEEN 9 AND 11) AND [SellerId] IS NULL)");
                });

            migrationBuilder.CreateTable(
                name: "LedgerJournals",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    SellerOrderId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    PaymentAttemptId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    PayoutId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    RefundId = table.Column<long>(type: "bigint", nullable: true),
                    BusinessKey = table.Column<string>(type: "nvarchar(120)", maxLength: 120, nullable: false),
                    Type = table.Column<int>(type: "int", nullable: false),
                    PostedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LedgerJournals", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "PaymentAttempts",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    OrderId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    SellerOrderId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
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
                    table.UniqueConstraint("AK_PaymentAttempts_Id_OrderId", x => new { x.Id, x.OrderId });
                    table.CheckConstraint("CK_PaymentAttempts_Amounts", "[AttemptNumber] > 0 AND [AmountVnd] >= 0 AND [CapturedAmountVnd] >= 0");
                });

            migrationBuilder.CreateTable(
                name: "PaymentOrderProjections",
                columns: table => new
                {
                    OrderId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    OrderNumber = table.Column<string>(type: "nvarchar(34)", maxLength: 34, nullable: false),
                    CustomerId = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: false),
                    CustomerEmail = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                    Status = table.Column<int>(type: "int", nullable: false),
                    PaymentDeadline = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    TotalVnd = table.Column<decimal>(type: "decimal(18,0)", precision: 18, scale: 0, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PaymentOrderProjections", x => x.OrderId);
                });

            migrationBuilder.CreateTable(
                name: "Payments",
                columns: table => new
                {
                    OrderId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Method = table.Column<int>(type: "int", nullable: false),
                    Status = table.Column<int>(type: "int", nullable: false),
                    AmountVnd = table.Column<decimal>(type: "decimal(18,0)", precision: 18, scale: 0, nullable: false),
                    PaidAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    ProviderTransaction = table.Column<string>(type: "nvarchar(120)", maxLength: 120, nullable: true),
                    CollectionReference = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    NextQueryAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    Issue = table.Column<string>(type: "nvarchar(250)", maxLength: 250, nullable: false),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Payments", x => x.OrderId);
                });

            migrationBuilder.CreateTable(
                name: "PaymentSellerMemberships",
                columns: table => new
                {
                    SellerId = table.Column<long>(type: "bigint", nullable: false),
                    UserId = table.Column<string>(type: "nvarchar(450)", nullable: false),
                    Role = table.Column<int>(type: "int", nullable: false),
                    Status = table.Column<int>(type: "int", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PaymentSellerMemberships", x => new { x.SellerId, x.UserId });
                });

            migrationBuilder.CreateTable(
                name: "PaymentSellerOrderProjections",
                columns: table => new
                {
                    SellerOrderId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    OrderId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    SellerId = table.Column<long>(type: "bigint", nullable: false),
                    SellerOrderNumber = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    Status = table.Column<int>(type: "int", nullable: false),
                    SubtotalVnd = table.Column<decimal>(type: "decimal(18,0)", precision: 18, scale: 0, nullable: false),
                    SellerDiscountVnd = table.Column<decimal>(type: "decimal(18,0)", precision: 18, scale: 0, nullable: false),
                    PlatformDiscountVnd = table.Column<decimal>(type: "decimal(18,0)", precision: 18, scale: 0, nullable: false),
                    ShippingVnd = table.Column<decimal>(type: "decimal(18,0)", precision: 18, scale: 0, nullable: false),
                    TotalVnd = table.Column<decimal>(type: "decimal(18,0)", precision: 18, scale: 0, nullable: false),
                    CommissionVnd = table.Column<decimal>(type: "decimal(18,0)", precision: 18, scale: 0, nullable: false),
                    DeliveredAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PaymentSellerOrderProjections", x => x.SellerOrderId);
                });

            migrationBuilder.CreateTable(
                name: "PaymentSellerProjections",
                columns: table => new
                {
                    SellerId = table.Column<long>(type: "bigint", nullable: false),
                    DisplayName = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    Status = table.Column<int>(type: "int", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PaymentSellerProjections", x => x.SellerId);
                });

            migrationBuilder.CreateTable(
                name: "Payouts",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    SellerId = table.Column<long>(type: "bigint", nullable: false),
                    SubmissionKey = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    AmountVnd = table.Column<decimal>(type: "decimal(18,0)", precision: 18, scale: 0, nullable: false),
                    Status = table.Column<int>(type: "int", nullable: false),
                    ProviderReference = table.Column<string>(type: "nvarchar(120)", maxLength: 120, nullable: false),
                    Issue = table.Column<string>(type: "nvarchar(250)", maxLength: 250, nullable: false),
                    ReviewNote = table.Column<string>(type: "nvarchar(250)", maxLength: 250, nullable: false),
                    RequestedBy = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: false),
                    RequestedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    ApprovedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    NextAttemptAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    CompletedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    Attempts = table.Column<int>(type: "int", nullable: false),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Payouts", x => x.Id);
                    table.CheckConstraint("CK_Payouts_AmountAndStatus", "[AmountVnd] > 0 AND [Status] BETWEEN 0 AND 4 AND [Attempts] >= 0");
                });

            migrationBuilder.CreateTable(
                name: "Refunds",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    OrderId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    PaymentAttemptId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    SellerOrderId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    ReturnId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    BusinessKey = table.Column<string>(type: "nvarchar(120)", maxLength: 120, nullable: false),
                    AmountVnd = table.Column<decimal>(type: "decimal(18,0)", precision: 18, scale: 0, nullable: false),
                    ShippingVnd = table.Column<decimal>(type: "decimal(18,0)", precision: 18, scale: 0, nullable: false),
                    Status = table.Column<int>(type: "int", nullable: false),
                    Simulated = table.Column<bool>(type: "bit", nullable: false),
                    Reference = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    ProviderReference = table.Column<string>(type: "nvarchar(120)", maxLength: 120, nullable: false),
                    Issue = table.Column<string>(type: "nvarchar(250)", maxLength: 250, nullable: false),
                    Attempts = table.Column<int>(type: "int", nullable: false),
                    NextAttemptAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    RequestedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    CompletedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Refunds", x => x.Id);
                    table.CheckConstraint("CK_Refunds_Amount", "[AmountVnd] > 0 AND [ShippingVnd] BETWEEN 0 AND [AmountVnd] AND ([ReturnId] IS NULL OR [SellerOrderId] IS NOT NULL)");
                });

            migrationBuilder.CreateTable(
                name: "SimulatedPayoutTransfers",
                columns: table => new
                {
                    PayoutId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ProviderReference = table.Column<string>(type: "nvarchar(120)", maxLength: 120, nullable: false),
                    AmountVnd = table.Column<decimal>(type: "decimal(18,0)", precision: 18, scale: 0, nullable: false),
                    Succeeded = table.Column<bool>(type: "bit", nullable: false),
                    ConfirmAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SimulatedPayoutTransfers", x => x.PayoutId);
                    table.CheckConstraint("CK_SimulatedPayoutTransfers_Amount", "[AmountVnd] > 0");
                });

            migrationBuilder.CreateTable(
                name: "LedgerEntries",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    JournalId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    AccountId = table.Column<long>(type: "bigint", nullable: false),
                    AmountVnd = table.Column<decimal>(type: "decimal(18,0)", precision: 18, scale: 0, nullable: false),
                    Side = table.Column<int>(type: "int", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LedgerEntries", x => x.Id);
                    table.CheckConstraint("CK_LedgerEntries_AmountAndSide", "[AmountVnd] > 0 AND [Side] IN (0, 1)");
                    table.ForeignKey(
                        name: "FK_LedgerEntries_LedgerAccounts_AccountId",
                        column: x => x.AccountId,
                        principalTable: "LedgerAccounts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_LedgerEntries_LedgerJournals_JournalId",
                        column: x => x.JournalId,
                        principalTable: "LedgerJournals",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "PaymentEvents",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    OrderId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    PaymentAttemptId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    EventType = table.Column<string>(type: "nvarchar(60)", maxLength: 60, nullable: false),
                    EventKey = table.Column<string>(type: "nvarchar(150)", maxLength: 150, nullable: false),
                    ReceivedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PaymentEvents", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PaymentEvents_PaymentAttempts_PaymentAttemptId",
                        column: x => x.PaymentAttemptId,
                        principalTable: "PaymentAttempts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "RefundItems",
                columns: table => new
                {
                    RefundId = table.Column<long>(type: "bigint", nullable: false),
                    OrderItemId = table.Column<long>(type: "bigint", nullable: false),
                    SellerOrderId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Quantity = table.Column<int>(type: "int", nullable: false),
                    AmountVnd = table.Column<decimal>(type: "decimal(18,0)", precision: 18, scale: 0, nullable: false),
                    CommissionReversalVnd = table.Column<decimal>(type: "decimal(18,0)", precision: 18, scale: 0, nullable: false),
                    PlatformDiscountReversalVnd = table.Column<decimal>(type: "decimal(18,0)", precision: 18, scale: 0, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RefundItems", x => new { x.RefundId, x.OrderItemId });
                    table.CheckConstraint("CK_RefundItems_Amounts", "[Quantity] > 0 AND [AmountVnd] >= 0 AND [CommissionReversalVnd] >= 0 AND [PlatformDiscountReversalVnd] >= 0");
                    table.ForeignKey(
                        name: "FK_RefundItems_Refunds_RefundId",
                        column: x => x.RefundId,
                        principalTable: "Refunds",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.InsertData(
                table: "PaymentSellerProjections",
                columns: new[] { "SellerId", "DisplayName", "Status" },
                values: new object[] { 1L, "HKShop", 1 });

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
                name: "IX_LedgerAccounts_SellerId_Type",
                table: "LedgerAccounts",
                columns: new[] { "SellerId", "Type" },
                unique: true,
                filter: "[SellerId] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_LedgerAccounts_Type",
                table: "LedgerAccounts",
                column: "Type",
                unique: true,
                filter: "[SellerId] IS NULL");

            migrationBuilder.CreateIndex(
                name: "IX_LedgerEntries_AccountId_JournalId",
                table: "LedgerEntries",
                columns: new[] { "AccountId", "JournalId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_LedgerEntries_JournalId",
                table: "LedgerEntries",
                column: "JournalId");

            migrationBuilder.CreateIndex(
                name: "IX_LedgerJournals_BusinessKey",
                table: "LedgerJournals",
                column: "BusinessKey",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_LedgerJournals_SellerOrderId_Type",
                table: "LedgerJournals",
                columns: new[] { "SellerOrderId", "Type" });

            migrationBuilder.CreateIndex(
                name: "IX_PaymentAttempts_OrderId_AppliedToOrder",
                table: "PaymentAttempts",
                column: "OrderId",
                unique: true,
                filter: "[AppliedToOrder] = 1 AND [SellerOrderId] IS NULL");

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
                name: "IX_PaymentAttempts_SellerOrderId",
                table: "PaymentAttempts",
                column: "SellerOrderId",
                unique: true,
                filter: "[AppliedToOrder] = 1 AND [SellerOrderId] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_PaymentAttempts_Status_NextQueryAt",
                table: "PaymentAttempts",
                columns: new[] { "Status", "NextQueryAt" });

            migrationBuilder.CreateIndex(
                name: "IX_PaymentEvents_EventKey",
                table: "PaymentEvents",
                column: "EventKey",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_PaymentEvents_PaymentAttemptId_ReceivedAt",
                table: "PaymentEvents",
                columns: new[] { "PaymentAttemptId", "ReceivedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_PaymentOrderProjections_Status_PaymentDeadline",
                table: "PaymentOrderProjections",
                columns: new[] { "Status", "PaymentDeadline" });

            migrationBuilder.CreateIndex(
                name: "IX_Payments_NextQueryAt",
                table: "Payments",
                column: "NextQueryAt");

            migrationBuilder.CreateIndex(
                name: "IX_Payments_PaidAt",
                table: "Payments",
                column: "PaidAt");

            migrationBuilder.CreateIndex(
                name: "IX_Payments_ProviderTransaction",
                table: "Payments",
                column: "ProviderTransaction",
                unique: true,
                filter: "[ProviderTransaction] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_PaymentSellerOrderProjections_OrderId",
                table: "PaymentSellerOrderProjections",
                column: "OrderId");

            migrationBuilder.CreateIndex(
                name: "IX_Payouts_ProviderReference",
                table: "Payouts",
                column: "ProviderReference",
                unique: true,
                filter: "[ProviderReference] <> N''");

            migrationBuilder.CreateIndex(
                name: "IX_Payouts_SellerId_RequestedAt",
                table: "Payouts",
                columns: new[] { "SellerId", "RequestedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_Payouts_SellerId_SubmissionKey",
                table: "Payouts",
                columns: new[] { "SellerId", "SubmissionKey" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Payouts_Status_NextAttemptAt",
                table: "Payouts",
                columns: new[] { "Status", "NextAttemptAt" });

            migrationBuilder.CreateIndex(
                name: "IX_Refunds_BusinessKey",
                table: "Refunds",
                column: "BusinessKey",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Refunds_CompletedAt",
                table: "Refunds",
                column: "CompletedAt");

            migrationBuilder.CreateIndex(
                name: "IX_Refunds_Id_SellerOrderId",
                table: "Refunds",
                columns: new[] { "Id", "SellerOrderId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Refunds_ReturnId",
                table: "Refunds",
                column: "ReturnId",
                unique: true,
                filter: "[ReturnId] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_Refunds_Status_NextAttemptAt",
                table: "Refunds",
                columns: new[] { "Status", "NextAttemptAt" });

            migrationBuilder.CreateIndex(
                name: "IX_Refunds_Status_RequestedAt",
                table: "Refunds",
                columns: new[] { "Status", "RequestedAt" });
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
                name: "LedgerEntries");

            migrationBuilder.DropTable(
                name: "PaymentEvents");

            migrationBuilder.DropTable(
                name: "PaymentOrderProjections");

            migrationBuilder.DropTable(
                name: "Payments");

            migrationBuilder.DropTable(
                name: "PaymentSellerMemberships");

            migrationBuilder.DropTable(
                name: "PaymentSellerOrderProjections");

            migrationBuilder.DropTable(
                name: "PaymentSellerProjections");

            migrationBuilder.DropTable(
                name: "Payouts");

            migrationBuilder.DropTable(
                name: "RefundItems");

            migrationBuilder.DropTable(
                name: "SimulatedPayoutTransfers");

            migrationBuilder.DropTable(
                name: "LedgerAccounts");

            migrationBuilder.DropTable(
                name: "LedgerJournals");

            migrationBuilder.DropTable(
                name: "PaymentAttempts");

            migrationBuilder.DropTable(
                name: "Refunds");
        }
    }
}
