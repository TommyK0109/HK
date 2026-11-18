using System.Data;
using System.Formats.Tar;
using System.Globalization;
using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Data.SqlClient;

namespace HKShop.AboImport;

public static class AboCatalogImport
{
    private const string ImagePrefix = "images/small/";
    private const int BatchSize = 2000;

    public static async Task Run(string connectionString, string mediaPath, string archiveDirectory)
    {
        var imageArchive = Path.Combine(archiveDirectory, "abo-images-small.tar");
        var listingArchive = Path.Combine(archiveDirectory, "abo-listings.tar");
        using var listings = new TarReader(File.OpenRead(listingArchive));
        var (imageRows, files) = ReadImageIndex(imageArchive);

        await using var db = new SqlConnection(connectionString);
        await db.OpenAsync();
        await Execute(db, """
            DECLARE @result int;
            EXEC @result = sp_getapplock @Resource = 'HKShop:AboCatalogImport',
                @LockMode = 'Exclusive', @LockOwner = 'Session', @LockTimeout = 0;
            IF @result < 0 THROW 50001, 'An ABO import is already running.', 1;
            IF OBJECT_ID('dbo.AboListings') IS NULL OR OBJECT_ID('dbo.AboImages') IS NULL
                OR OBJECT_ID('dbo.ProductTranslations') IS NULL
                THROW 50002, 'Apply the AboCatalogData migration before importing.', 1;

            CREATE TABLE #Images (
                ImageId nvarchar(32) COLLATE Latin1_General_100_BIN2 PRIMARY KEY,
                SourcePath nvarchar(256) NOT NULL,
                FileName nvarchar(50) NOT NULL,
                Width int NOT NULL,
                Height int NOT NULL
            );
            CREATE TABLE #Listings (
                DomainName nvarchar(100) COLLATE DATABASE_DEFAULT NOT NULL,
                ItemId nvarchar(32) COLLATE DATABASE_DEFAULT NOT NULL,
                Slug nvarchar(150) COLLATE DATABASE_DEFAULT NOT NULL,
                Name nvarchar(150) NOT NULL,
                Description nvarchar(4000) NOT NULL,
                Category nvarchar(100) COLLATE DATABASE_DEFAULT NOT NULL,
                DefaultLanguageTag nvarchar(64) NOT NULL,
                MetadataJson nvarchar(max) NOT NULL,
                PRIMARY KEY (DomainName, ItemId)
            );
            CREATE TABLE #Translations (
                DomainName nvarchar(100) COLLATE DATABASE_DEFAULT NOT NULL,
                ItemId nvarchar(32) COLLATE DATABASE_DEFAULT NOT NULL,
                LanguageTag nvarchar(64) COLLATE DATABASE_DEFAULT NOT NULL,
                Name nvarchar(max) NULL,
                Description nvarchar(max) NULL
            );
            CREATE TABLE #Links (
                DomainName nvarchar(100) COLLATE DATABASE_DEFAULT NOT NULL,
                ItemId nvarchar(32) COLLATE DATABASE_DEFAULT NOT NULL,
                ImageId nvarchar(32) COLLATE Latin1_General_100_BIN2 NOT NULL,
                Position int NOT NULL
            );
            """);

        Console.WriteLine($"Preparing {files.Count:N0} catalog image files.");
        CopyImages(imageArchive, mediaPath, files);
        await BulkCopy(db, null, "#Images", imageRows);
        await Execute(db, """
            INSERT dbo.AboImages (ImageId, SourcePath, FileName, Width, Height)
            SELECT ImageId, SourcePath, FileName, Width, Height
            FROM #Images AS image
            WHERE NOT EXISTS (SELECT 1 FROM dbo.AboImages AS saved WHERE saved.ImageId = image.ImageId);
            DROP TABLE #Images;
            """);

        using var listingRows = Rows(("DomainName", typeof(string)), ("ItemId", typeof(string)),
            ("Slug", typeof(string)), ("Name", typeof(string)), ("Description", typeof(string)),
            ("Category", typeof(string)), ("DefaultLanguageTag", typeof(string)), ("MetadataJson", typeof(string)));
        using var translations = Rows(("DomainName", typeof(string)), ("ItemId", typeof(string)),
            ("LanguageTag", typeof(string)), ("Name", typeof(string)), ("Description", typeof(string)));
        using var links = Rows(("DomainName", typeof(string)), ("ItemId", typeof(string)),
            ("ImageId", typeof(string)), ("Position", typeof(int)));

        var total = 0;
        for (var entry = listings.GetNextEntry(); entry is not null; entry = listings.GetNextEntry())
        {
            if (entry.DataStream is not { } data || !entry.Name.StartsWith("listings/metadata/listings_", StringComparison.Ordinal)
                || !entry.Name.EndsWith(".json.gz", StringComparison.Ordinal))
                continue;

            using var gzip = new GZipStream(data, CompressionMode.Decompress);
            using var reader = new StreamReader(gzip);
            while (await reader.ReadLineAsync() is { } line)
            {
                AddListing(line, listingRows, translations, links);
                total++;
                if (listingRows.Rows.Count < BatchSize)
                    continue;

                await SaveBatch(db, listingRows, translations, links);
                if (total % 10000 == 0)
                    Console.WriteLine($"Processed {total:N0} listings.");
            }
        }

        if (listingRows.Rows.Count > 0)
            await SaveBatch(db, listingRows, translations, links);
        Console.WriteLine($"Processed {total:N0} listings and {imageRows.Rows.Count:N0} indexed images.");
    }

    private static (DataTable Rows, Dictionary<string, string> Files) ReadImageIndex(string archive)
    {
        using var tar = new TarReader(File.OpenRead(archive));
        for (var entry = tar.GetNextEntry(); entry is not null; entry = tar.GetNextEntry())
        {
            if (entry.Name != "images/metadata/images.csv.gz" || entry.DataStream is not { } data)
                continue;

            var rows = Rows(("ImageId", typeof(string)), ("SourcePath", typeof(string)),
                ("FileName", typeof(string)), ("Width", typeof(int)), ("Height", typeof(int)));
            var files = new Dictionary<string, string>(StringComparer.Ordinal);
            using var gzip = new GZipStream(data, CompressionMode.Decompress);
            using var reader = new StreamReader(gzip);
            if (reader.ReadLine() != "image_id,height,width,path")
                throw new InvalidDataException("Unrecognized ABO image index header.");
            while (reader.ReadLine() is { } line)
            {
                var parts = line.Split(',');
                if (parts.Length != 4)
                    throw new InvalidDataException("Invalid ABO image index row.");
                var sourcePath = parts[3];
                var extension = Path.GetExtension(sourcePath).ToLowerInvariant();
                if (extension is not (".jpg" or ".png" or ".webp"))
                    throw new InvalidDataException($"Unsupported image format in ABO image index: {extension}");
                var hash = SHA256.HashData(Encoding.UTF8.GetBytes("abo:" + sourcePath));
                var fileName = Convert.ToHexStringLower(hash.AsSpan(0, 16)) + extension;
                files[ImagePrefix + sourcePath] = fileName;
                rows.Rows.Add(parts[0], sourcePath, fileName,
                    int.Parse(parts[2], CultureInfo.InvariantCulture), int.Parse(parts[1], CultureInfo.InvariantCulture));
            }
            return (rows, files);
        }
        throw new InvalidDataException("The ABO Small archive is missing its image index.");
    }

    private static void CopyImages(string archive, string mediaPath, Dictionary<string, string> files)
    {
        Directory.CreateDirectory(mediaPath);
        var existing = Directory.EnumerateFiles(mediaPath).Select(Path.GetFileName)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        using var tar = new TarReader(File.OpenRead(archive));
        var count = 0;
        var pending = new List<(string Path, byte[] Bytes)>(256);
        for (var entry = tar.GetNextEntry(); entry is not null; entry = tar.GetNextEntry())
        {
            if (entry.DataStream is not { } source || !files.Remove(entry.Name, out var fileName))
                continue;

            var target = Path.Combine(mediaPath, fileName);
            if (!existing.Contains(fileName) || new FileInfo(target).Length != entry.Length)
            {
                var bytes = new byte[checked((int)entry.Length)];
                source.ReadExactly(bytes);
                pending.Add((target, bytes));
                if (pending.Count == 256)
                    WriteImageBatch(pending);
            }

            if (++count % 25000 == 0)
                Console.WriteLine($"Processed {count:N0} image files.");
        }
        WriteImageBatch(pending);
        if (files.Count > 0)
            throw new InvalidDataException($"The ABO Small archive is missing {files.Count:N0} indexed image files.");
    }

    private static void WriteImageBatch(List<(string Path, byte[] Bytes)> images)
    {
        Parallel.ForEach(images, new ParallelOptions { MaxDegreeOfParallelism = 12 },
            image => File.WriteAllBytes(image.Path, image.Bytes));
        images.Clear();
    }

    private static void AddListing(string line, DataTable listings, DataTable translations, DataTable links)
    {
        using var document = JsonDocument.Parse(line);
        var item = document.RootElement;
        var domain = item.GetProperty("domain_name").GetString()!.ToLowerInvariant();
        var id = item.GetProperty("item_id").GetString()!;
        var names = Localized(item, "item_name");
        var descriptions = Localized(item, "product_description");
        foreach (var (tag, text) in Localized(item, "bullet_point"))
            descriptions.TryAdd(tag, text);

        var defaultTag = names.ContainsKey("en-US") ? "en-US"
            : names.Keys.FirstOrDefault(tag => tag.StartsWith("en-", StringComparison.Ordinal))
                ?? names.Keys.FirstOrDefault() ?? "und";
        var categoryKey = "ABO";
        if (item.TryGetProperty("product_type", out var types) && types.ValueKind == JsonValueKind.Array)
        {
            var type = types.EnumerateArray().FirstOrDefault();
            if (type.ValueKind == JsonValueKind.Object && type.TryGetProperty("value", out var value))
                categoryKey = value.GetString() ?? categoryKey;
        }
        var category = CultureInfo.InvariantCulture.TextInfo.ToTitleCase(categoryKey.Replace('_', ' ').ToLowerInvariant());
        var slug = $"abo-{domain.Replace('.', '-')}-{id.ToLowerInvariant()}";
        listings.Rows.Add(domain, id, slug, Limit(names.GetValueOrDefault(defaultTag, id), 150),
            Limit(descriptions.GetValueOrDefault(defaultTag, ""), 4000), Limit(category, 100), defaultTag, line);

        foreach (var tag in names.Keys.Union(descriptions.Keys))
        {
            if (tag != defaultTag)
                translations.Rows.Add(domain, id, tag, names.GetValueOrDefault(tag), descriptions.GetValueOrDefault(tag));
        }

        var imageIds = new HashSet<string>(StringComparer.Ordinal);
        var position = 0;
        if (item.TryGetProperty("main_image_id", out var main) && main.GetString() is { Length: > 0 } mainId)
        {
            imageIds.Add(mainId);
            links.Rows.Add(domain, id, mainId, position++);
        }
        if (item.TryGetProperty("other_image_id", out var other) && other.ValueKind == JsonValueKind.Array)
        {
            foreach (var image in other.EnumerateArray())
                if (image.GetString() is { Length: > 0 } otherId && imageIds.Add(otherId))
                    links.Rows.Add(domain, id, otherId, position++);
        }
    }

    private static Dictionary<string, string> Localized(JsonElement item, string property)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        if (!item.TryGetProperty(property, out var entries) || entries.ValueKind != JsonValueKind.Array)
            return result;

        foreach (var entry in entries.EnumerateArray())
        {
            if (!entry.TryGetProperty("value", out var value) || value.ValueKind != JsonValueKind.String)
                continue;

            var rawTag = entry.TryGetProperty("language_tag", out var language) ? language.GetString() : null;
            var tag = string.IsNullOrWhiteSpace(rawTag) ? "und" : NormalizeTag(rawTag);
            var text = WebUtility.HtmlDecode(Regex.Replace(value.GetString()!, "<[^>]*>", " ")).Trim();
            if (text.Length > 0)
                result.TryAdd(tag, text);
        }
        return result;
    }

    private static string NormalizeTag(string tag)
    {
        var parts = tag.Trim().Replace('_', '-').ToLowerInvariant().Split('-');
        for (var i = 1; i < parts.Length; i++)
        {
            if (parts[i].Length == 2)
                parts[i] = parts[i].ToUpperInvariant();
            else if (parts[i].Length == 4)
                parts[i] = CultureInfo.InvariantCulture.TextInfo.ToTitleCase(parts[i]);
        }
        return string.Join('-', parts);
    }

    private static string Limit(string value, int length) => value.Length <= length
        ? value
        : value[..(char.IsHighSurrogate(value[length - 1]) ? length - 1 : length)];

    private static async Task SaveBatch(SqlConnection db, DataTable listings, DataTable translations, DataTable links)
    {
        using var transaction = db.BeginTransaction();
        await BulkCopy(db, transaction, "#Listings", listings);
        await BulkCopy(db, transaction, "#Translations", translations);
        await BulkCopy(db, transaction, "#Links", links);
        await Execute(db, """
            INSERT dbo.Categories (Name, Active)
            SELECT DISTINCT listing.Category, 1
            FROM #Listings AS listing
            WHERE NOT EXISTS (SELECT 1 FROM dbo.Categories AS c WHERE c.Name = listing.Category);

            INSERT dbo.Products (CategoryId, Name, Slug, Description, Status)
            SELECT category.Id, listing.Name, listing.Slug, listing.Description, 0
            FROM #Listings AS listing
            JOIN dbo.Categories AS category ON category.Name = listing.Category
            WHERE NOT EXISTS (
                SELECT 1 FROM dbo.AboListings AS saved
                WHERE saved.DomainName = listing.DomainName AND saved.ItemId = listing.ItemId);

            INSERT dbo.AboListings (DomainName, ItemId, ProductId, DefaultLanguageTag, MetadataJson)
            SELECT listing.DomainName, listing.ItemId, product.Id, listing.DefaultLanguageTag, listing.MetadataJson
            FROM #Listings AS listing
            JOIN dbo.Products AS product ON product.Slug = listing.Slug
            WHERE NOT EXISTS (
                SELECT 1 FROM dbo.AboListings AS saved
                WHERE saved.DomainName = listing.DomainName AND saved.ItemId = listing.ItemId);

            INSERT dbo.ProductTranslations (ProductId, LanguageTag, Name, Description)
            SELECT listing.ProductId, translation.LanguageTag, translation.Name, translation.Description
            FROM #Translations AS translation
            JOIN dbo.AboListings AS listing ON listing.DomainName = translation.DomainName AND listing.ItemId = translation.ItemId
            WHERE NOT EXISTS (
                SELECT 1 FROM dbo.ProductTranslations AS saved
                WHERE saved.ProductId = listing.ProductId AND saved.LanguageTag = translation.LanguageTag);

            WITH images AS (
                SELECT listing.ProductId, asset.FileName, MIN(link.Position) AS Position
                FROM #Links AS link
                JOIN dbo.AboListings AS listing ON listing.DomainName = link.DomainName AND listing.ItemId = link.ItemId
                JOIN dbo.AboImages AS asset ON asset.ImageId = link.ImageId
                GROUP BY listing.ProductId, asset.FileName
            )
            INSERT dbo.ProductImages (ProductId, FileName, Alt)
            SELECT images.ProductId, images.FileName, product.Name
            FROM images
            JOIN dbo.Products AS product ON product.Id = images.ProductId
            WHERE NOT EXISTS (
                SELECT 1 FROM dbo.ProductImages AS saved
                WHERE saved.ProductId = images.ProductId AND saved.FileName = images.FileName)
            ORDER BY images.ProductId, images.Position, images.FileName;

            TRUNCATE TABLE #Listings;
            TRUNCATE TABLE #Translations;
            TRUNCATE TABLE #Links;
            """, transaction);
        await transaction.CommitAsync();
        listings.Clear();
        translations.Clear();
        links.Clear();
    }

    private static DataTable Rows(params (string Name, Type Type)[] columns)
    {
        var table = new DataTable { Locale = CultureInfo.InvariantCulture };
        foreach (var (name, type) in columns)
            table.Columns.Add(name, type);
        return table;
    }

    private static async Task BulkCopy(SqlConnection db, SqlTransaction? transaction, string destination, DataTable rows)
    {
        using var bulk = new SqlBulkCopy(db, SqlBulkCopyOptions.Default, transaction)
        {
            DestinationTableName = destination,
            BulkCopyTimeout = 120
        };
        foreach (DataColumn column in rows.Columns)
            bulk.ColumnMappings.Add(column.ColumnName, column.ColumnName);
        await bulk.WriteToServerAsync(rows);
    }

    private static async Task Execute(SqlConnection db, string sql, SqlTransaction? transaction = null)
    {
        using var command = new SqlCommand(sql, db, transaction) { CommandTimeout = 120 };
        await command.ExecuteNonQueryAsync();
    }
}
