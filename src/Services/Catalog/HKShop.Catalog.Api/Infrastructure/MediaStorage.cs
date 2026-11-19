using System.Net;
using Amazon;
using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;
using HKShop.Application;
using HKShop.Application.Catalog;
using HKShop.Domain;

namespace HKShop.Catalog.Api.Infrastructure;

public sealed record CatalogImageUpload(byte[] Data, string Extension, string ContentType)
{
    public const string ImmutableCacheControl = "public, max-age=31536000, immutable";

    public static async Task<CatalogImageUpload> Read(Stream source, int maxBytes, CancellationToken ct = default)
    {
        using var data = new MemoryStream();
        var buffer = new byte[32_768];
        int count;
        while ((count = await source.ReadAsync(buffer.AsMemory(), ct)) != 0)
        {
            if (data.Length + count > maxBytes)
                throw new ShopRuleException($"Images must not exceed {maxBytes / (1024 * 1024)} MB.");
            await data.WriteAsync(buffer.AsMemory(0, count), ct);
        }

        var bytes = data.ToArray();
        if (bytes.Length >= 20 && bytes.AsSpan(0, 8).SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }) &&
            bytes.AsSpan(12, 4).SequenceEqual("IHDR"u8)) return new(bytes, ".png", "image/png");
        if (bytes.Length >= 16 && bytes.AsSpan(0, 4).SequenceEqual("RIFF"u8) &&
            bytes.AsSpan(8, 4).SequenceEqual("WEBP"u8)) return new(bytes, ".webp", "image/webp");
        if (bytes.Length >= 5 && bytes[0] == 0xff && bytes[1] == 0xd8 && bytes[2] == 0xff &&
            bytes[^2] == 0xff && bytes[^1] == 0xd9) return new(bytes, ".jpg", "image/jpeg");
        throw new ShopRuleException("Only JPEG, PNG, or WebP images are accepted.");
    }

    public static bool IsValidFileName(string name)
    {
        if (string.IsNullOrEmpty(name) || name != Path.GetFileName(name)) return false;
        var extension = Path.GetExtension(name);
        return (extension is ".jpg" or ".png" or ".webp")
            && Guid.TryParseExact(Path.GetFileNameWithoutExtension(name), "N", out _);
    }

    public static string? ContentTypeForExtension(string extension) => extension.ToLowerInvariant() switch
    {
        ".jpg" or ".jpeg" => "image/jpeg",
        ".png" => "image/png",
        ".webp" => "image/webp",
        _ => null
    };
}

public sealed class CatalogLocalProductImages(ShopSettings settings) : IProductImageStore
{
    public const int MaxBytes = 5 * 1024 * 1024;

    public async Task<string> Save(Stream source)
    {
        var upload = await CatalogImageUpload.Read(source, MaxBytes);
        var name = $"{Guid.NewGuid():N}{upload.Extension}";
        Directory.CreateDirectory(settings.MediaPath);
        await File.WriteAllBytesAsync(Path.Combine(settings.MediaPath, name), upload.Data);
        return name;
    }

    public Task Delete(string name)
    {
        var path = PathFor(name);
        if (path is not null && File.Exists(path)) File.Delete(path);
        return Task.CompletedTask;
    }

    public Task<ProductImageContent?> OpenRead(string name, CancellationToken cancellationToken = default)
    {
        var path = PathFor(name);
        if (path is null || !File.Exists(path))
            return Task.FromResult<ProductImageContent?>(null);

        var contentType = CatalogImageUpload.ContentTypeForExtension(Path.GetExtension(path)) ?? "application/octet-stream";
        return Task.FromResult<ProductImageContent?>(new ProductImageContent(File.OpenRead(path), contentType));
    }

    public string Url(string name) => $"/media/{Uri.EscapeDataString(name)}";

    private string? PathFor(string name) =>
        CatalogImageUpload.IsValidFileName(name) ? Path.Combine(settings.MediaPath, name) : null;
}

public sealed class CatalogS3MediaSettings
{
    public string ServiceUrl { get; set; } = "";
    public string Region { get; set; } = "auto";
    public string Bucket { get; set; } = "";
    public string AccessKey { get; set; } = "";
    public string SecretKey { get; set; } = "";
    public string KeyPrefix { get; set; } = "products";
    public string PublicBaseUrl { get; set; } = "";
    public bool ForcePathStyle { get; set; } = true;
    public bool? DisablePayloadSigning { get; set; }

    public bool Enabled => !string.IsNullOrWhiteSpace(Bucket);
}

public sealed class CatalogS3ProductImages : IProductImageStore, IDisposable
{
    private readonly IAmazonS3 client;
    private readonly CatalogS3MediaSettings settings;
    private readonly string prefix;
    private readonly string publicBaseUrl;

    public CatalogS3ProductImages(CatalogS3MediaSettings settings)
    {
        this.settings = settings;
        client = CreateClient(settings);
        prefix = string.Join('/', settings.KeyPrefix.Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        publicBaseUrl = settings.PublicBaseUrl.Trim().TrimEnd('/');
    }

    public async Task<string> Save(Stream source)
    {
        var upload = await CatalogImageUpload.Read(source, CatalogLocalProductImages.MaxBytes);
        var name = $"{Guid.NewGuid():N}{upload.Extension}";
        await using var body = new MemoryStream(upload.Data, writable: false);
        var request = new PutObjectRequest
        {
            BucketName = settings.Bucket,
            Key = ObjectKey(name),
            InputStream = body,
            AutoCloseStream = false,
            ContentType = upload.ContentType,
            DisablePayloadSigning = settings.DisablePayloadSigning ??
                (!string.IsNullOrWhiteSpace(settings.ServiceUrl) && settings.ServiceUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase)),
            DisableDefaultChecksumValidation = true
        };
        request.Headers.CacheControl = CatalogImageUpload.ImmutableCacheControl;
        await client.PutObjectAsync(request);
        return name;
    }

    public async Task Delete(string name)
    {
        if (!CatalogImageUpload.IsValidFileName(name)) return;
        await client.DeleteObjectAsync(settings.Bucket, ObjectKey(name));
    }

    public async Task<ProductImageContent?> OpenRead(string name, CancellationToken cancellationToken = default)
    {
        if (!CatalogImageUpload.IsValidFileName(name)) return null;
        try
        {
            var response = await client.GetObjectAsync(settings.Bucket, ObjectKey(name), cancellationToken);
            var contentType = CatalogImageUpload.ContentTypeForExtension(Path.GetExtension(name))
                ?? response.Headers.ContentType ?? "application/octet-stream";
            return new ProductImageContent(response.ResponseStream, contentType, response);
        }
        catch (AmazonS3Exception error) when (error.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }
    }

    public string Url(string name) =>
        publicBaseUrl.Length == 0 ? $"/media/{Uri.EscapeDataString(name)}" : $"{publicBaseUrl}/{ObjectKey(Uri.EscapeDataString(name))}";

    private string ObjectKey(string name) => prefix.Length == 0 ? name : $"{prefix}/{name}";

    private static AmazonS3Client CreateClient(CatalogS3MediaSettings settings)
    {
        var config = new AmazonS3Config
        {
            ForcePathStyle = settings.ForcePathStyle,
            RequestChecksumCalculation = RequestChecksumCalculation.WHEN_REQUIRED,
            ResponseChecksumValidation = ResponseChecksumValidation.WHEN_REQUIRED
        };
        if (!string.IsNullOrWhiteSpace(settings.ServiceUrl))
        {
            config.ServiceURL = settings.ServiceUrl;
            if (!string.IsNullOrWhiteSpace(settings.Region))
                config.AuthenticationRegion = settings.Region;
        }
        else if (!string.IsNullOrWhiteSpace(settings.Region))
        {
            config.RegionEndpoint = RegionEndpoint.GetBySystemName(settings.Region);
        }

        return new AmazonS3Client(new BasicAWSCredentials(settings.AccessKey, settings.SecretKey), config);
    }

    public void Dispose() => client.Dispose();
}
