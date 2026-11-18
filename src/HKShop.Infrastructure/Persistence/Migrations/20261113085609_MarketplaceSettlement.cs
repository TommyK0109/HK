using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace HKShop.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class MarketplaceSettlement : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_RefundItems_Amounts",
                table: "RefundItems");

            migrationBuilder.AddColumn<decimal>(
                name: "CommissionReversalVnd",
                table: "RefundItems",
                type: "decimal(18,0)",
                precision: 18,
                scale: 0,
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<decimal>(
                name: "PlatformDiscountReversalVnd",
                table: "RefundItems",
                type: "decimal(18,0)",
                precision: 18,
                scale: 0,
                nullable: false,
                defaultValue: 0m);

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
                    table.CheckConstraint("CK_LedgerAccounts_Scope", "([Type] BETWEEN 6 AND 8 AND [SellerId] IS NOT NULL) OR (([Type] BETWEEN 0 AND 5 OR [Type] = 9) AND [SellerId] IS NULL)");
                    table.ForeignKey(
                        name: "FK_LedgerAccounts_Sellers_SellerId",
                        column: x => x.SellerId,
                        principalTable: "Sellers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
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
                    table.ForeignKey(
                        name: "FK_Payouts_AspNetUsers_RequestedBy",
                        column: x => x.RequestedBy,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Payouts_Sellers_SellerId",
                        column: x => x.SellerId,
                        principalTable: "Sellers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
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
                    table.ForeignKey(
                        name: "FK_LedgerJournals_PaymentAttempts_PaymentAttemptId",
                        column: x => x.PaymentAttemptId,
                        principalTable: "PaymentAttempts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_LedgerJournals_Payouts_PayoutId",
                        column: x => x.PayoutId,
                        principalTable: "Payouts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_LedgerJournals_Refunds_RefundId",
                        column: x => x.RefundId,
                        principalTable: "Refunds",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_LedgerJournals_SellerOrders_SellerOrderId",
                        column: x => x.SellerOrderId,
                        principalTable: "SellerOrders",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
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
                    table.ForeignKey(
                        name: "FK_SimulatedPayoutTransfers_Payouts_PayoutId",
                        column: x => x.PayoutId,
                        principalTable: "Payouts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
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

            migrationBuilder.AddCheckConstraint(
                name: "CK_RefundItems_Amounts",
                table: "RefundItems",
                sql: "[Quantity] > 0 AND [AmountVnd] >= 0 AND [CommissionReversalVnd] >= 0 AND [PlatformDiscountReversalVnd] >= 0");

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
                name: "IX_LedgerJournals_PaymentAttemptId",
                table: "LedgerJournals",
                column: "PaymentAttemptId");

            migrationBuilder.CreateIndex(
                name: "IX_LedgerJournals_PayoutId",
                table: "LedgerJournals",
                column: "PayoutId");

            migrationBuilder.CreateIndex(
                name: "IX_LedgerJournals_RefundId",
                table: "LedgerJournals",
                column: "RefundId");

            migrationBuilder.CreateIndex(
                name: "IX_LedgerJournals_SellerOrderId_Type",
                table: "LedgerJournals",
                columns: new[] { "SellerOrderId", "Type" });

            migrationBuilder.CreateIndex(
                name: "IX_Payouts_ProviderReference",
                table: "Payouts",
                column: "ProviderReference",
                unique: true,
                filter: "[ProviderReference] <> N''");

            migrationBuilder.CreateIndex(
                name: "IX_Payouts_RequestedBy",
                table: "Payouts",
                column: "RequestedBy");

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

            migrationBuilder.Sql("""
                EXEC(N'CREATE TRIGGER dbo.TR_LedgerJournals_AppendOnly ON dbo.LedgerJournals AFTER UPDATE, DELETE AS
                BEGIN
                    SET NOCOUNT ON;
                    IF EXISTS (SELECT 1 FROM deleted)
                        THROW 51000, ''Posted settlement journals are append-only.'', 1;
                END');

                EXEC(N'CREATE TRIGGER dbo.TR_LedgerEntries_AppendOnly ON dbo.LedgerEntries AFTER UPDATE, DELETE AS
                BEGIN
                    SET NOCOUNT ON;
                    IF EXISTS (SELECT 1 FROM deleted)
                        THROW 51000, ''Posted settlement entries are append-only.'', 1;
                END');
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                IF EXISTS (SELECT 1 FROM dbo.LedgerJournals) OR EXISTS (SELECT 1 FROM dbo.Payouts)
                    THROW 51000, 'Rolling back settlement would discard journals or payout history.', 1;
                """);

            migrationBuilder.DropTable(
                name: "LedgerEntries");

            migrationBuilder.DropTable(
                name: "SimulatedPayoutTransfers");

            migrationBuilder.DropTable(
                name: "LedgerAccounts");

            migrationBuilder.DropTable(
                name: "LedgerJournals");

            migrationBuilder.DropTable(
                name: "Payouts");

            migrationBuilder.DropCheckConstraint(
                name: "CK_RefundItems_Amounts",
                table: "RefundItems");

            migrationBuilder.DropColumn(
                name: "CommissionReversalVnd",
                table: "RefundItems");

            migrationBuilder.DropColumn(
                name: "PlatformDiscountReversalVnd",
                table: "RefundItems");

            migrationBuilder.AddCheckConstraint(
                name: "CK_RefundItems_Amounts",
                table: "RefundItems",
                sql: "[Quantity] > 0 AND [AmountVnd] >= 0");
        }
    }
}
