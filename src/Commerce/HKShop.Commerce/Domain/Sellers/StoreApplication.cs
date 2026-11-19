using HKShop.Contracts.Ports;

namespace HKShop.Domain.Sellers;

public sealed class StoreApplication
{
    public Guid Id { get; set; }
    public string UserId { get; set; } = "";
    public string Email { get; set; } = "";
    public string StoreName { get; set; } = "";
    public string ProductsJson { get; set; } = "";
    public StoreApplicationStatus Status { get; set; }
    public DateTimeOffset SubmittedAt { get; set; }
    public DateTimeOffset? DecidedAt { get; set; }
    public string? ReviewerId { get; set; }
    public string? DeclineReason { get; set; }
    public long? SellerId { get; set; }
    public DateTimeOffset? ReadAt { get; set; }
}

public sealed class StoreApplicationVerification
{
    public Guid ApplicationId { get; set; }
    public string NationalId { get; set; } = "";
    public string BusinessLicenseNumber { get; set; } = "";
}

public sealed class StoreVerificationDocument
{
    public Guid ApplicationId { get; set; }
    public StoreDocumentKind Kind { get; set; }
    public byte[] Data { get; set; } = [];
    public string ContentType { get; set; } = "";
}
