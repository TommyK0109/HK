using HKShop.Domain;

namespace HKShop.Infrastructure.Media;

public sealed record ImageUpload(byte[] Data, string Extension, string ContentType)
{
    public const string ImmutableCacheControl = "public, max-age=31536000, immutable";

    public static async Task<ImageUpload> Read(Stream source, int maxBytes, CancellationToken cancellationToken = default)
    {
        using var data = new MemoryStream();
        var buffer = new byte[32_768];
        int count;
        while ((count = await source.ReadAsync(buffer.AsMemory(), cancellationToken)) != 0)
        {
            if (data.Length + count > maxBytes)
                throw new ShopRuleException($"Images must not exceed {maxBytes / (1024 * 1024)} MB.");
            await data.WriteAsync(buffer.AsMemory(0, count), cancellationToken);
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
