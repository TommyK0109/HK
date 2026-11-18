using HKShop.Application;
using HKShop.Application.Catalog;

namespace HKShop.Infrastructure.Media;

public sealed class LocalProductImages(ShopSettings settings) : IProductImageStore
{
    public const int MaxBytes = 5 * 1024 * 1024;

    public async Task<string> Save(Stream source)
    {
        var upload = await ImageUpload.Read(source, MaxBytes);
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

        var contentType = ImageUpload.ContentTypeForExtension(Path.GetExtension(path)) ?? "application/octet-stream";
        return Task.FromResult<ProductImageContent?>(new ProductImageContent(File.OpenRead(path), contentType));
    }

    public string Url(string name) => $"/media/{Uri.EscapeDataString(name)}";

    public string? PathFor(string name) =>
        ImageUpload.IsValidFileName(name) ? Path.Combine(settings.MediaPath, name) : null;
}
