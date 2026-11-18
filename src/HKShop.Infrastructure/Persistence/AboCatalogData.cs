namespace HKShop.Infrastructure.Persistence;

public sealed class AboListing
{
    public string DomainName { get; set; } = "";
    public string ItemId { get; set; } = "";
    public int ProductId { get; set; }
    public string DefaultLanguageTag { get; set; } = "";
    public string MetadataJson { get; set; } = "";
}

public sealed class ProductTranslation
{
    public int ProductId { get; set; }
    public string LanguageTag { get; set; } = "";
    public string? Name { get; set; }
    public string? Description { get; set; }
}

public sealed class AboImage
{
    public string ImageId { get; set; } = "";
    public string SourcePath { get; set; } = "";
    public string FileName { get; set; } = "";
    public int Width { get; set; }
    public int Height { get; set; }
}
