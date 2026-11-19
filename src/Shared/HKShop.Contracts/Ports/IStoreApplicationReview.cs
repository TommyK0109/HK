using HKShop.Application;

namespace HKShop.Contracts.Ports;

public enum StoreApplicationStatus { Pending, Approved, Declined }
public enum StoreDocumentKind { NationalIdFront, NationalIdBack, BusinessLicense }

public sealed record StoreApplicationProduct(string Name, decimal PriceVnd);
public sealed record StoreApplicationSummary(Guid Id, string StoreName, string Email, StoreApplicationStatus Status,
    DateTimeOffset SubmittedAt, DateTimeOffset? DecidedAt, long? SellerId, string? DeclineReason, DateTimeOffset? ReadAt);
public sealed record StoreApplicationReview(StoreApplicationSummary Summary, IReadOnlyList<StoreApplicationProduct> Products,
    string? NationalId, string? BusinessLicenseNumber);
public sealed record StoreApplicationDocument(byte[] Data, string ContentType);

public interface IStoreApplicationReview
{
    Task<Page<StoreApplicationSummary>> Queue(ShopActor reviewer, int page);
    Task<StoreApplicationReview?> Details(ShopActor reviewer, Guid id);
    Task<StoreApplicationDocument?> Document(ShopActor reviewer, Guid id, StoreDocumentKind kind);
    Task Decide(ShopActor reviewer, Guid id, bool approve, string? declineReason);
}
