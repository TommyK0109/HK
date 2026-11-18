using Amazon.S3.Model;
using HKShop.Infrastructure.Media;
using Microsoft.Extensions.Configuration;

if (args is not [var mediaPath])
{
    Console.Error.WriteLine("Set S3__* environment variables and pass the local media directory.");
    Console.Error.WriteLine("Example: dotnet run --project tools/HKShop.MediaSync -- .\\src\\HKShop.Web\\data\\media");
    return 1;
}

if (!Directory.Exists(mediaPath))
{
    Console.Error.WriteLine($"Media directory not found: {Path.GetFullPath(mediaPath)}");
    return 1;
}

var configuration = new ConfigurationBuilder().AddEnvironmentVariables().Build();
var settings = configuration.GetSection("S3").Get<S3MediaSettings>() ?? new();
if (!settings.Enabled)
{
    Console.Error.WriteLine("Set S3__Bucket, S3__AccessKey, S3__SecretKey, and S3__ServiceUrl (or S3__Region) before running.");
    return 1;
}

using var client = S3ProductImages.CreateClient(settings);
var prefix = string.Join('/', settings.KeyPrefix.Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
var listPrefix = prefix.Length == 0 ? null : $"{prefix}/";

Console.WriteLine($"Listing existing objects in s3://{settings.Bucket}/{prefix}...");
var existing = new Dictionary<string, long>(StringComparer.Ordinal);
string? token = null;
do
{
    var page = await client.ListObjectsV2Async(new ListObjectsV2Request
    {
        BucketName = settings.Bucket,
        Prefix = listPrefix,
        ContinuationToken = token
    });
    if (page.S3Objects is not null)
    {
        foreach (var item in page.S3Objects)
            existing[item.Key] = item.Size ?? -1;
    }
    token = page.IsTruncated == true ? page.NextContinuationToken : null;
}
while (token is not null);

var files = Directory.EnumerateFiles(mediaPath)
    .Select(path => new
    {
        Path = path,
        Name = Path.GetFileName(path),
        ContentType = ImageUpload.ContentTypeForExtension(Path.GetExtension(path))
    })
    .Where(item => item.ContentType is not null)
    .ToList();

Console.WriteLine($"Found {files.Count:N0} image files locally ({existing.Count:N0} objects already in bucket prefix).");

var disablePayloadSigning = S3ProductImages.ShouldDisablePayloadSigning(settings);
long uploaded = 0;
long skipped = 0;
long failed = 0;
long processed = 0;

await Parallel.ForEachAsync(files, new ParallelOptions { MaxDegreeOfParallelism = 16 }, async (file, cancellationToken) =>
{
    var key = prefix.Length == 0 ? file.Name : $"{prefix}/{file.Name}";
    var info = new FileInfo(file.Path);
    if (existing.TryGetValue(key, out var remoteSize) && remoteSize == info.Length)
    {
        Interlocked.Increment(ref skipped);
    }
    else
    {
        try
        {
            await using var stream = File.OpenRead(file.Path);
            var request = new PutObjectRequest
            {
                BucketName = settings.Bucket,
                Key = key,
                InputStream = stream,
                AutoCloseStream = false,
                ContentType = file.ContentType!,
                DisablePayloadSigning = disablePayloadSigning,
                DisableDefaultChecksumValidation = true
            };
            request.Headers.CacheControl = ImageUpload.ImmutableCacheControl;
            await client.PutObjectAsync(request, cancellationToken);
            Interlocked.Increment(ref uploaded);
        }
        catch (Exception error)
        {
            Interlocked.Increment(ref failed);
            Console.Error.WriteLine($"Failed {file.Name}: {error.Message}");
        }
    }

    var done = Interlocked.Increment(ref processed);
    if (done % 2000 == 0)
        Console.WriteLine($"Processed {done:N0}/{files.Count:N0} (uploaded {Interlocked.Read(ref uploaded):N0}, skipped {Interlocked.Read(ref skipped):N0}).");
});

Console.WriteLine($"Done. Uploaded: {uploaded:N0}, Skipped: {skipped:N0}, Failed: {failed:N0}.");
return failed > 0 ? 1 : 0;
