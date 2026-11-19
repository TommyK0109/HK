using System.Data;
using System.Security.Cryptography;
using HKShop.Catalog.Api.Infrastructure;
using HKShop.Identity.Api.Infrastructure;
using HKShop.Notification;
using HKShop.Ordering.Api.Infrastructure;
using HKShop.Payments.Api.Infrastructure;
using HKShop.Shipping.Api.Infrastructure;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;

var config = new ConfigurationBuilder()
    .AddEnvironmentVariables()
    .AddCommandLine(args, new Dictionary<string, string>
    {
        ["--source"] = "ConnectionStrings:HKShop",
        ["--identity"] = "ConnectionStrings:Identity",
        ["--catalog"] = "ConnectionStrings:Catalog",
        ["--ordering"] = "ConnectionStrings:Ordering",
        ["--payments"] = "ConnectionStrings:Payments",
        ["--shipping"] = "ConnectionStrings:Shipping",
        ["--notification"] = "ConnectionStrings:Notification"
    })
    .Build();

return await DataSplit.RunAsync(config);

public static class DataSplit
{
    public static async Task<int> RunAsync(IConfiguration config)
    {
        var sourceConn = config.GetConnectionString("HKShop");
        var identityConn = config.GetConnectionString("Identity");
        var catalogConn = config.GetConnectionString("Catalog");
        var orderingConn = config.GetConnectionString("Ordering");
        var paymentsConn = config.GetConnectionString("Payments");
        var shippingConn = config.GetConnectionString("Shipping");
        var notificationConn = config.GetConnectionString("Notification");

        if (new[] { sourceConn, identityConn, catalogConn, orderingConn, paymentsConn, shippingConn }.Any(string.IsNullOrWhiteSpace))
        {
            Console.Error.WriteLine(
                "Usage: HKShop.DataSplit --source <HKShop> --identity <Identity> --catalog <Catalog> --ordering <Ordering> --payments <Payments> --shipping <Shipping>");
            return 1;
        }

        var databases = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var connection in new[] { sourceConn, identityConn, catalogConn, orderingConn, paymentsConn, shippingConn, notificationConn }.Where(c => !string.IsNullOrWhiteSpace(c)))
        {
            var settings = new SqlConnectionStringBuilder(connection);
            var database = settings.InitialCatalog;
            if (string.IsNullOrWhiteSpace(database) || new[] { "master", "model", "msdb", "tempdb" }.Contains(database, StringComparer.OrdinalIgnoreCase))
                throw new InvalidOperationException("DataSplit requires named application databases.");
            settings.InitialCatalog = "master";
            await using var server = new SqlConnection(settings.ConnectionString);
            await server.OpenAsync();
            await using var identify = new SqlCommand("SELECT SERVERPROPERTY('ServerName')", server);
            if (!databases.Add($"{await identify.ExecuteScalarAsync()}/{database}"))
                throw new InvalidOperationException("The source and every service must use separate databases.");
        }

        await EnsureSchemaAsync(new IdentityServiceDbContext(new DbContextOptionsBuilder<IdentityServiceDbContext>().UseSqlServer(identityConn!).Options));
        await EnsureSchemaAsync(new CatalogDbContext(new DbContextOptionsBuilder<CatalogDbContext>().UseSqlServer(catalogConn!).Options));
        await EnsureSchemaAsync(new OrderingDbContext(new DbContextOptionsBuilder<OrderingDbContext>().UseSqlServer(orderingConn!).Options));
        await EnsureSchemaAsync(new PaymentsDbContext(new DbContextOptionsBuilder<PaymentsDbContext>().UseSqlServer(paymentsConn!).Options));
        await EnsureSchemaAsync(new ShippingDbContext(new DbContextOptionsBuilder<ShippingDbContext>().UseSqlServer(shippingConn!).Options));
        if (!string.IsNullOrWhiteSpace(notificationConn))
            await EnsureSchemaAsync(new NotificationDbContext(new DbContextOptionsBuilder<NotificationDbContext>().UseSqlServer(notificationConn).Options));

        var identityTables = new[]
        {
            "AspNetRoles", "AspNetRoleClaims", "AspNetUsers", "AspNetUserClaims",
            "AspNetUserLogins", "AspNetUserRoles", "AspNetUserTokens", "UserProfileImages", "CustomerAddresses"
        };

        var catalogTables = new[]
        {
            "Categories", "Sellers", "SellerLocations", "Products", "Skus", "StockBalances", "StockMovements",
            "ProductImages", "ProductReviews", "AboListings", "ProductTranslations", "AboImages",
            "SellerMemberships", "SellerInvitations", "SellerOffers", "InventoryBalances", "OfferStockMovements",
            "ProductProposals", "StoreApplications", "StoreApplicationVerifications", "StoreVerificationDocuments"
        };

        var orderingTables = new[]
        {
            "ShoppingCarts", "CartItems", "Promotions", "Orders", "SellerOrders", "OrderItems", "OrderEvents",
            "Shipments", "ReturnRequests", "ReturnItems", "CheckoutRequests", "PromotionClaims", "AuditEntries"
        };

        var paymentsTables = new[]
        {
            "Payments", "PaymentAttempts", "PaymentEvents", "Refunds", "RefundItems",
            "LedgerAccounts", "LedgerJournals", "LedgerEntries", "Payouts", "SimulatedPayoutTransfers"
        };

        var shippingTables = new[]
        {
            "TransportShippers", "TransportParcels", "TransportAssignments", "TransportBankReceipts"
        };

        var failures = 0;

        await using var src = new SqlConnection(sourceConn);
        await src.OpenAsync();

        failures += await CopyAndReconcileServiceAsync(src, "Identity", identityConn!, identityTables, clearSeedTables: []);
        failures += await CopyAndReconcileServiceAsync(src, "Catalog", catalogConn!, catalogTables, clearSeedTables: ["SellerLocations", "Sellers"]);
        failures += await CopyAndReconcileServiceAsync(src, "Ordering", orderingConn!, orderingTables, clearSeedTables: []);
        failures += await CopyAndReconcileServiceAsync(src, "Payments", paymentsConn!, paymentsTables, clearSeedTables: []);
        failures += await CopyAndReconcileServiceAsync(src, "Shipping", shippingConn!, shippingTables, clearSeedTables: []);

        // Populate read projections in Ordering, Payments, and Shipping from the source database
        if (failures > 0) return 1;
        await PopulateProjectionsAsync(src, orderingConn!, paymentsConn!, shippingConn!);
        if (!string.IsNullOrWhiteSpace(notificationConn))
            await CopyProjectionAsync(src, notificationConn, "NotificationMessages", """
                SELECT Id, EventKey, Recipient, Subject, Body, Attempts, NextAttemptAt, SentAt FROM dbo.OutboxMessages
                """, await CountRowsAsync(src, "OutboxMessages"));
        await CopyProjectionAsync(src, catalogConn!, "CatalogStockHolds", """
            SELECT r.Id, r.OrderId, so.Id AS SellerOrderId, r.OfferId, r.SkuId, b.SellerId,
                r.InventoryBalanceId, r.Quantity, r.Status, o.CreatedAt
            FROM dbo.StockReservations r
            JOIN dbo.InventoryBalances b ON b.Id = r.InventoryBalanceId
            JOIN dbo.Orders o ON o.Id = r.OrderId
            JOIN dbo.SellerOrders so ON so.OrderId = r.OrderId AND so.SellerId = b.SellerId
            """, await CountRowsAsync(src, "StockReservations"));
        await CopyProjectionAsync(src, shippingConn!, "ShippingOrderSnapshots", """
            SELECT so.Id AS SellerOrderId, o.Id AS OrderId, so.SellerId, so.Number AS SellerOrderNumber,
                so.Status AS SellerOrderStatus, so.TotalVnd, so.ShippingVnd, p.Method AS PaymentMethod,
                o.PaymentStatus, o.Recipient, o.Phone, o.ShippingAddress,
                CAST(0 AS bit) AS CancelledBeforePlacement
            FROM dbo.SellerOrders so JOIN dbo.Orders o ON o.Id = so.OrderId
            JOIN dbo.Payments p ON p.OrderId = o.Id
            """, await CountRowsAsync(src, "SellerOrders"));

        // Reconcile financial and inventory checksums
        failures += await ReconcileChecksumAsync(src, orderingConn!, "Orders.TotalVnd", "SELECT COALESCE(SUM(TotalVnd), 0) FROM dbo.Orders");
        failures += await ReconcileChecksumAsync(src, paymentsConn!, "Payments.AmountVnd", "SELECT COALESCE(SUM(AmountVnd), 0) FROM dbo.Payments");
        failures += await ReconcileChecksumAsync(src, paymentsConn!, "LedgerEntries.AmountVnd", "SELECT COALESCE(SUM(AmountVnd), 0) FROM dbo.LedgerEntries");
        failures += await ReconcileChecksumAsync(src, catalogConn!, "InventoryBalances.OnHand", "SELECT COALESCE(SUM(CAST(OnHand AS BIGINT)), 0) FROM dbo.InventoryBalances");
        failures += await ReconcileChecksumAsync(src, catalogConn!, "InventoryBalances.Reserved", "SELECT COALESCE(SUM(CAST(Reserved AS BIGINT)), 0) FROM dbo.InventoryBalances");

        Console.WriteLine(failures == 0
            ? "DataSplit completed: all table counts and checksums match."
            : $"DataSplit completed with {failures} reconciliation mismatch(es).");
        return failures == 0 ? 0 : 1;
    }

    static async Task EnsureSchemaAsync(DbContext db)
    {
        await using (db)
        {
            await db.Database.MigrateAsync();
        }
    }

    static async Task<int> CopyAndReconcileServiceAsync(
        SqlConnection src,
        string serviceName,
        string targetConn,
        string[] tables,
        string[] clearSeedTables)
    {
        var mismatches = 0;
        await using var dst = new SqlConnection(targetConn);
        await dst.OpenAsync();

        // Clear pre-seeded rows (e.g. Seller.FirstPartyId) before copying if target is otherwise empty
        foreach (var table in tables)
        {
            if (!await TableExistsAsync(src, table) || !await TableExistsAsync(dst, table))
                throw new InvalidOperationException($"[{serviceName}] Required table dbo.{table} is missing.");
        }

        var seedOnly = clearSeedTables.Length > 0;
        foreach (var table in tables.Except(clearSeedTables))
            if (await CountRowsAsync(dst, table) > 0) { seedOnly = false; break; }
        foreach (var seedTable in seedOnly ? clearSeedTables : [])
        {
            if (await CountRowsAsync(src, seedTable) > 0 && await CountRowsAsync(dst, seedTable) <= 1)
            {
                await using var clearCmd = new SqlCommand($"DELETE FROM dbo.[{seedTable}]", dst);
                await clearCmd.ExecuteNonQueryAsync();
            }
        }

        foreach (var table in tables)
        {
            var srcCount = await CountRowsAsync(src, table);
            var dstCount = await CountRowsAsync(dst, table);

            var sourceQuery = table switch
                {
                    "Orders" => "(SELECT o.*, p.Method AS PaymentMethod FROM dbo.Orders o JOIN dbo.Payments p ON p.OrderId = o.Id) AS orders",
                    "OrderItems" => "(SELECT i.*, s.ProductId FROM dbo.OrderItems i JOIN dbo.Skus s ON s.Id = i.SkuId) AS items",
                    _ => $"dbo.[{table}]"
                };
            var commonColumns = await GetCommonNonComputedColumnsAsync(src, dst, table);
            var columns = string.Join(", ", commonColumns.Select(c => $"[{c}]"));
            if (dstCount == 0 && srcCount > 0)
            {
                if (commonColumns.Count > 0)
                {
                    var selectSql = $"SELECT {columns} FROM {sourceQuery}";
                    await using var readCmd = new SqlCommand(selectSql, src) { CommandTimeout = 120 };
                    await using var reader = await readCmd.ExecuteReaderAsync(CommandBehavior.SequentialAccess);
                    using var bulk = new SqlBulkCopy(dst, SqlBulkCopyOptions.KeepIdentity | SqlBulkCopyOptions.CheckConstraints | SqlBulkCopyOptions.TableLock, null)
                    {
                        DestinationTableName = $"dbo.[{table}]",
                        BatchSize = 1000,
                        BulkCopyTimeout = 300
                    };
                    foreach (var col in commonColumns)
                        bulk.ColumnMappings.Add(col, col);
                    await bulk.WriteToServerAsync(reader);
                }
                dstCount = await CountRowsAsync(dst, table);
            }

            Console.WriteLine($"[{serviceName}] {table}: source={srcCount}, target={dstCount}");
            if (srcCount != dstCount) mismatches++;
            else if (srcCount > 0)
            {
                var hashes = await Task.WhenAll(ContentHashAsync(src, sourceQuery, columns), ContentHashAsync(dst, $"dbo.[{table}]", columns));
                if (!hashes[0].SequenceEqual(hashes[1]))
                {
                    Console.Error.WriteLine($"[{serviceName}] {table}: copied values differ.");
                    mismatches++;
                }
            }
        }

        return mismatches;
    }

    static async Task<byte[]> ContentHashAsync(SqlConnection connection, string source, string columns)
    {
        // Sorting the fixed-size row hashes also works for composite keys and preserves duplicate rows.
        var sql = $"SELECT HASHBYTES('SHA2_256', (SELECT {columns} FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES)) AS digest FROM {source} ORDER BY digest";
        await using var command = new SqlCommand(sql, connection) { CommandTimeout = 600 };
        await using var reader = await command.ExecuteReaderAsync();
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        while (await reader.ReadAsync()) hash.AppendData((byte[])reader[0]);
        return hash.GetHashAndReset();
    }

    static async Task PopulateProjectionsAsync(SqlConnection src, string orderingConn, string paymentsConn, string shippingConn)
    {
        await using (var ord = new SqlConnection(orderingConn))
        {
            await ord.OpenAsync();
            if (await CountRowsAsync(ord, "OrderingSellerMemberships") == 0 && await TableExistsAsync(src, "SellerMemberships"))
            {
                await using var cmd = new SqlCommand("SELECT SellerId, UserId, Role, Status FROM dbo.SellerMemberships", src);
                await using var r = await cmd.ExecuteReaderAsync();
                using var bulk = new SqlBulkCopy(ord) { DestinationTableName = "dbo.OrderingSellerMemberships", BatchSize = 1000 };
                foreach (var c in new[] { "SellerId", "UserId", "Role", "Status" }) bulk.ColumnMappings.Add(c, c);
                await bulk.WriteToServerAsync(r);
            }
        }

        await using (var pay = new SqlConnection(paymentsConn))
        {
            await pay.OpenAsync();
            if (await CountRowsAsync(pay, "PaymentOrderProjections") == 0 && await TableExistsAsync(src, "Orders"))
            {
                await using var cmd = new SqlCommand(
                    "SELECT Id AS OrderId, Number AS OrderNumber, CustomerId, CustomerEmail, Status, PaymentDeadline, TotalVnd, CreatedAt FROM dbo.Orders", src);
                await using var r = await cmd.ExecuteReaderAsync();
                using var bulk = new SqlBulkCopy(pay) { DestinationTableName = "dbo.PaymentOrderProjections", BatchSize = 1000 };
                foreach (var c in new[] { "OrderId", "OrderNumber", "CustomerId", "CustomerEmail", "Status", "PaymentDeadline", "TotalVnd", "CreatedAt" })
                    bulk.ColumnMappings.Add(c, c);
                await bulk.WriteToServerAsync(r);
            }
            if (await CountRowsAsync(pay, "PaymentSellerOrderProjections") == 0 && await TableExistsAsync(src, "SellerOrders"))
            {
                await using var cmd = new SqlCommand(
                    "SELECT Id AS SellerOrderId, OrderId, SellerId, Number AS SellerOrderNumber, Status, SubtotalVnd, SellerDiscountVnd, PlatformDiscountVnd, ShippingVnd, TotalVnd, CommissionVnd, DeliveredAt FROM dbo.SellerOrders", src);
                await using var r = await cmd.ExecuteReaderAsync();
                using var bulk = new SqlBulkCopy(pay) { DestinationTableName = "dbo.PaymentSellerOrderProjections", BatchSize = 1000 };
                foreach (var c in new[] { "SellerOrderId", "OrderId", "SellerId", "SellerOrderNumber", "Status", "SubtotalVnd", "SellerDiscountVnd", "PlatformDiscountVnd", "ShippingVnd", "TotalVnd", "CommissionVnd", "DeliveredAt" })
                    bulk.ColumnMappings.Add(c, c);
                await bulk.WriteToServerAsync(r);
            }
            if (await CountRowsAsync(pay, "PaymentSellerProjections") <= 1 && await TableExistsAsync(src, "Sellers"))
            {
                await using var clear = new SqlCommand("DELETE FROM dbo.PaymentSellerProjections", pay);
                await clear.ExecuteNonQueryAsync();
                await using var cmd = new SqlCommand("SELECT Id AS SellerId, DisplayName, Status FROM dbo.Sellers", src);
                await using var r = await cmd.ExecuteReaderAsync();
                using var bulk = new SqlBulkCopy(pay) { DestinationTableName = "dbo.PaymentSellerProjections", BatchSize = 1000 };
                foreach (var c in new[] { "SellerId", "DisplayName", "Status" }) bulk.ColumnMappings.Add(c, c);
                await bulk.WriteToServerAsync(r);
            }
            if (await CountRowsAsync(pay, "PaymentSellerMemberships") == 0 && await TableExistsAsync(src, "SellerMemberships"))
            {
                await using var cmd = new SqlCommand("SELECT SellerId, UserId, Role, Status FROM dbo.SellerMemberships", src);
                await using var r = await cmd.ExecuteReaderAsync();
                using var bulk = new SqlBulkCopy(pay) { DestinationTableName = "dbo.PaymentSellerMemberships", BatchSize = 1000 };
                foreach (var c in new[] { "SellerId", "UserId", "Role", "Status" }) bulk.ColumnMappings.Add(c, c);
                await bulk.WriteToServerAsync(r);
            }
        }

        await using (var ship = new SqlConnection(shippingConn))
        {
            await ship.OpenAsync();
            if (await CountRowsAsync(ship, "ShippingSellerLocations") == 0 && await TableExistsAsync(src, "SellerLocations"))
            {
                await using var cmd = new SqlCommand("SELECT Id AS LocationId, SellerId, Name, PickupAddress, Latitude, Longitude, Active FROM dbo.SellerLocations", src);
                await using var r = await cmd.ExecuteReaderAsync();
                using var bulk = new SqlBulkCopy(ship) { DestinationTableName = "dbo.ShippingSellerLocations", BatchSize = 1000 };
                foreach (var c in new[] { "LocationId", "SellerId", "Name", "PickupAddress", "Latitude", "Longitude", "Active" })
                    bulk.ColumnMappings.Add(c, c);
                await bulk.WriteToServerAsync(r);
            }
            if (await CountRowsAsync(ship, "ShippingSellerMemberships") == 0 && await TableExistsAsync(src, "SellerMemberships"))
            {
                await using var cmd = new SqlCommand("SELECT SellerId, UserId, Role, Status FROM dbo.SellerMemberships", src);
                await using var r = await cmd.ExecuteReaderAsync();
                using var bulk = new SqlBulkCopy(ship) { DestinationTableName = "dbo.ShippingSellerMemberships", BatchSize = 1000 };
                foreach (var c in new[] { "SellerId", "UserId", "Role", "Status" }) bulk.ColumnMappings.Add(c, c);
                await bulk.WriteToServerAsync(r);
            }
        }
    }

    static async Task CopyProjectionAsync(SqlConnection src, string targetConn, string table, string query, long expected)
    {
        await using var dst = new SqlConnection(targetConn);
        await dst.OpenAsync();
        var count = await CountRowsAsync(dst, table);
        if (count == 0 && expected > 0)
        {
            await using var cmd = new SqlCommand(query, src);
            await using var reader = await cmd.ExecuteReaderAsync();
            using var bulk = new SqlBulkCopy(dst, SqlBulkCopyOptions.KeepIdentity | SqlBulkCopyOptions.CheckConstraints, null)
            {
                DestinationTableName = $"dbo.[{table}]",
                BulkCopyTimeout = 300
            };
            for (var i = 0; i < reader.FieldCount; i++)
                bulk.ColumnMappings.Add(reader.GetName(i), reader.GetName(i));
            await bulk.WriteToServerAsync(reader);
            count = await CountRowsAsync(dst, table);
        }
        if (count != expected)
            throw new InvalidOperationException($"Projection {table} expected {expected} rows but contains {count}.");
    }

    static async Task<int> ReconcileChecksumAsync(SqlConnection src, string targetConn, string label, string query)
    {
        await using var dst = new SqlConnection(targetConn);
        await dst.OpenAsync();
        var srcVal = Convert.ToDecimal(await new SqlCommand(query, src).ExecuteScalarAsync());
        var dstVal = Convert.ToDecimal(await new SqlCommand(query, dst).ExecuteScalarAsync());
        Console.WriteLine($"[Checksum] {label}: source={srcVal:N0}, target={dstVal:N0}");
        return srcVal == dstVal ? 0 : 1;
    }

    static async Task<bool> TableExistsAsync(SqlConnection conn, string table)
    {
        await using var cmd = new SqlCommand("SELECT COUNT(*) FROM sys.tables WHERE schema_id = SCHEMA_ID('dbo') AND name = @name", conn);
        cmd.Parameters.AddWithValue("@name", table);
        return Convert.ToInt32(await cmd.ExecuteScalarAsync()) > 0;
    }

    static async Task<long> CountRowsAsync(SqlConnection conn, string table)
    {
        await using var cmd = new SqlCommand($"SELECT COUNT_BIG(*) FROM dbo.[{table}]", conn);
        return Convert.ToInt64(await cmd.ExecuteScalarAsync());
    }

    static async Task<List<string>> GetCommonNonComputedColumnsAsync(SqlConnection src, SqlConnection dst, string table)
    {
        var srcCols = (await ReadColumnNamesAsync(src, table)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (table == "Orders") srcCols.Add("PaymentMethod");
        if (table == "OrderItems") srcCols.Add("ProductId");
        var dstCols = await ReadColumnNamesAsync(dst, table);
        return dstCols.Where(srcCols.Contains).ToList();
    }

    static async Task<List<string>> ReadColumnNamesAsync(SqlConnection conn, string table)
    {
        const string sql = """
            SELECT c.name
            FROM sys.columns c
            INNER JOIN sys.tables t ON c.object_id = t.object_id
            WHERE t.schema_id = SCHEMA_ID('dbo') AND t.name = @table
              AND c.is_computed = 0 AND TYPE_NAME(c.system_type_id) <> 'timestamp'
            ORDER BY c.column_id
            """;
        await using var cmd = new SqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("@table", table);
        var cols = new List<string>();
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync()) cols.Add(reader.GetString(0));
        return cols;
    }
}
