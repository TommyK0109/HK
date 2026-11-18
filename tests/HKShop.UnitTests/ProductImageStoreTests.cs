using System.Net;
using System.Reflection;
using Amazon.S3;
using Amazon.S3.Model;
using HKShop.Application;
using HKShop.Application.Catalog;
using HKShop.Domain;
using HKShop.Domain.Catalog;
using HKShop.Infrastructure.Media;
using Xunit;

namespace HKShop.UnitTests;

public sealed class ProductImageStoreTests
{
    private static readonly byte[] ValidPng =
    [
        137, 80, 78, 71, 13, 10, 26, 10,
        0, 0, 0, 13, (byte)'I', (byte)'H', (byte)'D', (byte)'R',
        0, 0, 0, 1, 0, 0, 0, 1, 8, 6, 0, 0, 0
    ];

    [Fact]
    public async Task LocalProductImages_SavesReadsDeletesAndBlocksTraversal()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"hkshop-media-test-{Guid.NewGuid():N}");
        try
        {
            var store = new LocalProductImages(new ShopSettings { MediaPath = tempDir });
            await using var upload = new MemoryStream(ValidPng);
            var name = await store.Save(upload);

            Assert.True(ImageUpload.IsValidFileName(name));
            Assert.EndsWith(".png", name, StringComparison.Ordinal);
            Assert.Equal($"/media/{name}", store.Url(name));
            Assert.True(File.Exists(Path.Combine(tempDir, name)));

            await using (var opened = await store.OpenRead(name))
            {
                Assert.NotNull(opened);
                Assert.Equal("image/png", opened!.ContentType);
                using var copy = new MemoryStream();
                await opened.Content.CopyToAsync(copy);
                Assert.Equal(ValidPng, copy.ToArray());
            }

            Assert.Null(await store.OpenRead("../evil.png"));
            Assert.Null(await store.OpenRead("not-a-guid.png"));

            await store.Delete(name);
            Assert.False(File.Exists(Path.Combine(tempDir, name)));
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public async Task LocalProductImages_RejectsInvalidImageBytes()
    {
        var store = new LocalProductImages(new ShopSettings { MediaPath = Path.GetTempPath() });
        await using var invalid = new MemoryStream("not an image"u8.ToArray());
        await Assert.ThrowsAsync<ShopRuleException>(() => store.Save(invalid));
    }

    [Fact]
    public async Task S3ProductImages_UploadsWithImmutableCacheAndR2Compatibility()
    {
        var proxy = FakeS3Proxy.Create();
        var settings = new S3MediaSettings
        {
            ServiceUrl = "https://account123.r2.cloudflarestorage.com",
            Bucket = "hkshop-media",
            AccessKey = "ak",
            SecretKey = "sk",
            KeyPrefix = "/products/",
            PublicBaseUrl = "https://media.example.com/"
        };
        var store = new S3ProductImages(proxy.Client, settings);

        await using var upload = new MemoryStream(ValidPng);
        var name = await store.Save(upload);

        Assert.NotNull(proxy.LastPut);
        Assert.Equal("hkshop-media", proxy.LastPut!.BucketName);
        Assert.Equal($"products/{name}", proxy.LastPut.Key);
        Assert.Equal("image/png", proxy.LastPut.ContentType);
        Assert.Equal(ImageUpload.ImmutableCacheControl, proxy.LastPut.Headers.CacheControl);
        Assert.True(proxy.LastPut.DisablePayloadSigning);
        Assert.True(proxy.LastPut.DisableDefaultChecksumValidation);
        Assert.Equal($"https://media.example.com/products/{name}", store.Url(name));

        await store.Delete(name);
        Assert.Equal(("hkshop-media", $"products/{name}"), proxy.LastDeleted);
    }

    [Fact]
    public void S3ProductImages_UrlAndPayloadSigningDefaultsMatchEndpointScheme()
    {
        var proxy = FakeS3Proxy.Create();
        var minio = new S3MediaSettings
        {
            ServiceUrl = "http://localhost:9000",
            Bucket = "hkshop-media",
            KeyPrefix = "",
            PublicBaseUrl = ""
        };
        var minioStore = new S3ProductImages(proxy.Client, minio);
        var file = $"{Guid.Empty:N}.jpg";

        Assert.False(S3ProductImages.ShouldDisablePayloadSigning(minio));
        Assert.Equal($"/media/{file}", minioStore.Url(file));
        Assert.Equal(file, minioStore.ObjectKey(file));
    }

    [Fact]
    public async Task S3ProductImages_OpenReadHandlesHitAnd404()
    {
        var proxy = FakeS3Proxy.Create();
        var name = $"{Guid.NewGuid():N}.png";
        proxy.Objects[$"products/{name}"] = ValidPng;
        var store = new S3ProductImages(proxy.Client, new S3MediaSettings
        {
            Bucket = "hkshop-media",
            KeyPrefix = "products"
        });

        await using (var found = await store.OpenRead(name))
        {
            Assert.NotNull(found);
            Assert.Equal("image/png", found!.ContentType);
            using var copy = new MemoryStream();
            await found.Content.CopyToAsync(copy);
            Assert.Equal(ValidPng, copy.ToArray());
        }

        Assert.Null(await store.OpenRead($"{Guid.NewGuid():N}.png"));
        Assert.Null(await store.OpenRead("../bad.png"));
    }

    [Fact]
    public async Task CatalogAddImage_DeletesUploadedFileWhenDatabaseWriteFails()
    {
        var imageStore = new RecordingImageStore();
        var catalog = new CatalogService(new FailingWriteShopStore(), imageStore, TimeProvider.System);
        var staff = new ShopActor("staff-1", "staff@example.test", true, Staff: true, Admin: false) { Mfa = true };

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            catalog.AddImage(staff, 1, "Front view", new MemoryStream(ValidPng)));

        Assert.Single(imageStore.Saved);
        Assert.Equal(imageStore.Saved, imageStore.Deleted);
    }

    private sealed class RecordingImageStore : IProductImageStore
    {
        public List<string> Saved { get; } = [];
        public List<string> Deleted { get; } = [];

        public Task<string> Save(Stream source)
        {
            var name = $"{Guid.NewGuid():N}.png";
            Saved.Add(name);
            return Task.FromResult(name);
        }

        public Task Delete(string name)
        {
            Deleted.Add(name);
            return Task.CompletedTask;
        }
    }

    private sealed class FailingWriteShopStore : IShopStore
    {
        public Task<T> Read<T>(Func<IShopSession, Task<T>> query) => query(new FakeSession());

        public Task<T> Write<T>(Func<IShopSession, Task<T>> action) => throw new InvalidOperationException("Simulated SQL failure.");

        private sealed class FakeSession : IShopSession
        {
            public IQueryable<T> Query<T>() where T : class => Enumerable.Empty<T>().AsQueryable();
            public Task<List<T>> List<T>(IQueryable<T> query) => Task.FromResult(new List<T>());
            public Task<T?> First<T>(IQueryable<T> query) => Task.FromResult((T?)(object?)new Product { Id = 1, Name = "Demo", Slug = "demo" });
            public Task<int> Count<T>(IQueryable<T> query) => Task.FromResult(0);
            public Task<decimal> Sum(IQueryable<decimal> query) => Task.FromResult(0m);
            public void Add<T>(T entity) where T : class { }
            public void Remove<T>(T entity) where T : class { }
            public Task Save() => Task.CompletedTask;
        }
    }

    private class FakeS3Proxy : DispatchProxy
    {
        public IAmazonS3 Client => (IAmazonS3)(object)this;
        public PutObjectRequest? LastPut { get; private set; }
        public (string Bucket, string Key)? LastDeleted { get; private set; }
        public Dictionary<string, byte[]> Objects { get; } = new(StringComparer.Ordinal);

        public static FakeS3Proxy Create() => (FakeS3Proxy)(object)Create<IAmazonS3, FakeS3Proxy>();

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            switch (targetMethod?.Name)
            {
                case nameof(IAmazonS3.PutObjectAsync):
                    LastPut = (PutObjectRequest)args![0]!;
                    return Task.FromResult(new PutObjectResponse());
                case nameof(IAmazonS3.DeleteObjectAsync):
                    LastDeleted = ((string)args![0]!, (string)args[1]!);
                    return Task.FromResult(new DeleteObjectResponse());
                case nameof(IAmazonS3.GetObjectAsync):
                    var key = (string)args![1]!;
                    if (!Objects.TryGetValue(key, out var bytes))
                    {
                        return Task.FromException<GetObjectResponse>(new AmazonS3Exception("Not Found")
                        {
                            StatusCode = HttpStatusCode.NotFound
                        });
                    }
                    return Task.FromResult(new GetObjectResponse
                    {
                        ResponseStream = new MemoryStream(bytes)
                    });
                case nameof(IDisposable.Dispose):
                    return null;
                default:
                    throw new NotSupportedException($"Unexpected S3 call: {targetMethod?.Name}");
            }
        }
    }
}
