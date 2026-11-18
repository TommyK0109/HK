using System.Net;
using Amazon;
using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;
using HKShop.Application.Catalog;

namespace HKShop.Infrastructure.Media;

public sealed class S3MediaSettings
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

public sealed class S3ProductImages : IProductImageStore, IDisposable
{
    private readonly IAmazonS3 client;
    private readonly S3MediaSettings settings;
    private readonly string prefix;
    private readonly string publicBaseUrl;
    private readonly bool ownsClient;

    public S3ProductImages(S3MediaSettings settings)
        : this(CreateClient(settings), settings, ownsClient: true)
    {
    }

    public S3ProductImages(IAmazonS3 client, S3MediaSettings settings)
        : this(client, settings, ownsClient: false)
    {
    }

    private S3ProductImages(IAmazonS3 client, S3MediaSettings settings, bool ownsClient)
    {
        this.client = client;
        this.settings = settings;
        prefix = string.Join('/', settings.KeyPrefix.Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        publicBaseUrl = settings.PublicBaseUrl.Trim().TrimEnd('/');
        this.ownsClient = ownsClient;
    }

    public async Task<string> Save(Stream source)
    {
        var upload = await ImageUpload.Read(source, LocalProductImages.MaxBytes);
        var name = $"{Guid.NewGuid():N}{upload.Extension}";
        await using var body = new MemoryStream(upload.Data, writable: false);
        var request = new PutObjectRequest
        {
            BucketName = settings.Bucket,
            Key = ObjectKey(name),
            InputStream = body,
            AutoCloseStream = false,
            ContentType = upload.ContentType,
            DisablePayloadSigning = ShouldDisablePayloadSigning(settings),
            DisableDefaultChecksumValidation = true
        };
        request.Headers.CacheControl = ImageUpload.ImmutableCacheControl;
        await client.PutObjectAsync(request);
        return name;
    }

    public async Task Delete(string name)
    {
        if (!ImageUpload.IsValidFileName(name)) return;
        await client.DeleteObjectAsync(settings.Bucket, ObjectKey(name));
    }

    public async Task<ProductImageContent?> OpenRead(string name, CancellationToken cancellationToken = default)
    {
        if (!ImageUpload.IsValidFileName(name)) return null;
        try
        {
            var response = await client.GetObjectAsync(settings.Bucket, ObjectKey(name), cancellationToken);
            var contentType = ImageUpload.ContentTypeForExtension(Path.GetExtension(name))
                ?? response.Headers.ContentType
                ?? "application/octet-stream";
            return new ProductImageContent(response.ResponseStream, contentType, response);
        }
        catch (AmazonS3Exception error) when (error.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }
    }

    public string Url(string name) =>
        publicBaseUrl.Length == 0
            ? $"/media/{Uri.EscapeDataString(name)}"
            : $"{publicBaseUrl}/{ObjectKey(Uri.EscapeDataString(name))}";

    public string ObjectKey(string name) => prefix.Length == 0 ? name : $"{prefix}/{name}";

    public static bool ShouldDisablePayloadSigning(S3MediaSettings settings) =>
        settings.DisablePayloadSigning ??
        (!string.IsNullOrWhiteSpace(settings.ServiceUrl) &&
            settings.ServiceUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase));

    public static AmazonS3Client CreateClient(S3MediaSettings settings)
    {
        if (string.IsNullOrWhiteSpace(settings.Bucket))
            throw new InvalidOperationException("Configure S3:Bucket before enabling S3 media storage.");
        if (string.IsNullOrWhiteSpace(settings.AccessKey) || string.IsNullOrWhiteSpace(settings.SecretKey))
            throw new InvalidOperationException("Configure S3:AccessKey and S3:SecretKey when S3:Bucket is set.");

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

    public void Dispose()
    {
        if (ownsClient) client.Dispose();
    }
}
