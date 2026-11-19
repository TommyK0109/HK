using System.Text.Json;
using HKShop.Contracts.Ports;
using HKShop.Domain;
using HKShop.Domain.Orders;
using HKShop.Domain.Sellers;
using Microsoft.AspNetCore.DataProtection;

namespace HKShop.Application.Sellers;

public sealed class StoreApplications(IShopStore store, TimeProvider time, IDataProtectionProvider protection) : IStoreApplicationReview
{
    private readonly IDataProtector protector = protection.CreateProtector("HKShop.StoreApplicationVerification.v1");

    public Task<Guid> Submit(ShopActor actor, Guid id, string name, string nationalId, string licenseNumber,
        IReadOnlyList<StoreApplicationProduct> products, IReadOnlyDictionary<StoreDocumentKind, StoreApplicationDocument> documents)
    {
        actor.RequireCustomer();
        name = ShopRules.Text(name, 100, "Store name");
        nationalId = ShopRules.Text(nationalId, 64, "National ID number");
        licenseNumber = ShopRules.Text(licenseNumber, 64, "Business license number");
        if (id == Guid.Empty || products.Count == 0 || !Enum.GetValues<StoreDocumentKind>().All(documents.ContainsKey))
            throw new ShopRuleException("Enter the product list and upload all three documents.");
        var normalized = products.Select(product =>
        {
            ShopRules.Vnd(product.PriceVnd);
            if (product.PriceVnd == 0) throw new ShopRuleException("Product prices must be greater than zero.");
            return product with { Name = ShopRules.Text(product.Name, 150, "Product name") };
        }).ToList();

        return store.Write(async db =>
        {
            var existing = await db.First(db.Query<StoreApplication>().Where(a => a.Id == id));
            if (existing is not null)
            {
                if (existing.UserId != actor.UserId) throw new SellerAccessDeniedException();
                return existing.Id;
            }

            db.Add(new StoreApplication
            {
                Id = id, UserId = actor.UserId, Email = actor.Email, StoreName = name,
                ProductsJson = JsonSerializer.Serialize(normalized), SubmittedAt = time.GetUtcNow()
            });
            db.Add(new StoreApplicationVerification
            {
                ApplicationId = id, NationalId = protector.Protect(nationalId),
                BusinessLicenseNumber = protector.Protect(licenseNumber)
            });
            foreach (var (kind, document) in documents)
            {
                db.Add(new StoreVerificationDocument
                {
                    ApplicationId = id, Kind = kind, Data = protector.Protect(document.Data), ContentType = document.ContentType
                });
            }
            return id;
        });
    }

    public Task<List<StoreApplicationSummary>> Mine(ShopActor actor)
    {
        actor.RequireCustomer();
        return store.Read(db => db.List(Summaries(db.Query<StoreApplication>().Where(a => a.UserId == actor.UserId)
            .OrderByDescending(a => a.SubmittedAt))));
    }

    public Task MarkRead(ShopActor actor, Guid id)
    {
        actor.RequireCustomer();
        return store.Write(async db =>
        {
            var row = await db.First(db.Query<StoreApplication>().Where(a => a.Id == id && a.UserId == actor.UserId &&
                a.Status != StoreApplicationStatus.Pending)) ?? throw new SellerAccessDeniedException();
            row.ReadAt ??= time.GetUtcNow();
            return true;
        });
    }

    public Task<Page<StoreApplicationSummary>> Queue(ShopActor reviewer, int page)
    {
        RequireReviewer(reviewer);
        return store.Read(async db =>
        {
            const int size = 25;
            var query = db.Query<StoreApplication>();
            var count = await db.Count(query);
            var number = Math.Clamp(page, 1, Math.Max(1, (count + size - 1) / size));
            var items = await db.List(Summaries(query.OrderBy(a => a.Status == StoreApplicationStatus.Pending ? 0 : 1)
                .ThenByDescending(a => a.SubmittedAt).ThenBy(a => a.Id).Skip((number - 1) * size).Take(size)));
            return new Page<StoreApplicationSummary>(items, number, count, size);
        });
    }

    public Task<StoreApplicationReview?> Details(ShopActor reviewer, Guid id)
    {
        RequireReviewer(reviewer);
        return store.Read(async db =>
        {
            var query = db.Query<StoreApplication>().Where(a => a.Id == id);
            var summary = await db.First(Summaries(query));
            if (summary is null) return null;
            var application = await db.First(query);
            var numbers = await db.First(db.Query<StoreApplicationVerification>().Where(a => a.ApplicationId == id));
            return new StoreApplicationReview(summary,
                JsonSerializer.Deserialize<List<StoreApplicationProduct>>(application!.ProductsJson)!,
                numbers is null ? null : protector.Unprotect(numbers.NationalId),
                numbers is null ? null : protector.Unprotect(numbers.BusinessLicenseNumber));
        });
    }

    public Task<StoreApplicationDocument?> Document(ShopActor reviewer, Guid id, StoreDocumentKind kind)
    {
        RequireReviewer(reviewer);
        return store.Read(async db =>
        {
            var row = await db.First(db.Query<StoreVerificationDocument>().Where(d => d.ApplicationId == id && d.Kind == kind));
            return row is null ? null : new StoreApplicationDocument(protector.Unprotect(row.Data), row.ContentType);
        });
    }

    public Task Decide(ShopActor reviewer, Guid id, bool approve, string? declineReason)
    {
        RequireReviewer(reviewer);
        var reason = approve ? null : ShopRules.Text(declineReason, 250, "Decline reason");
        return store.Write(async db =>
        {
            var row = await db.First(db.Query<StoreApplication>().Where(a => a.Id == id))
                ?? throw new ShopRuleException("The application was not found.");
            if (row.Status != StoreApplicationStatus.Pending)
                throw new ShopRuleException("The application has already been reviewed.");

            var now = time.GetUtcNow();
            if (approve)
            {
                var seller = new Seller
                {
                    Slug = $"store-{Guid.NewGuid():N}", DisplayName = row.StoreName, CreatedAt = now, Status = SellerStatus.Active
                };
                db.Add(seller);
                await db.Save();
                db.Add(new SellerMembership { SellerId = seller.Id, UserId = row.UserId, Email = row.Email, Role = SellerRole.Owner });
                row.SellerId = seller.Id;
            }

            row.Status = approve ? StoreApplicationStatus.Approved : StoreApplicationStatus.Declined;
            row.DecidedAt = now;
            row.ReviewerId = reviewer.UserId;
            row.DeclineReason = reason;
            db.Audit(reviewer.UserId, approve ? "Approve store application" : "Decline store application", id.ToString("D"),
                now, sellerId: row.SellerId);
            var decision = approve ? "approved" : "declined";
            db.Add(new OutboxMessage
            {
                EventKey = $"store-application-decision:{id:D}", Recipient = row.Email,
                Subject = $"Your HKShop store application was {decision}",
                Body = $"Your application for {row.StoreName} was {decision}. Sign in to HKShop and open Account → Register a store for details.",
                NextAttemptAt = now
            });
            return true;
        });
    }

    public Task Close(ShopActor actor, long sellerId) => store.Write(async db =>
    {
        var workspace = await SellerAccess.Require(db, actor, sellerId, SellerPermission.Workspace);
        if (sellerId == Seller.FirstPartyId || workspace.Role != SellerRole.Owner || workspace.Status == SellerStatus.Pending)
            throw new SellerAccessDeniedException();

        var seller = await db.First(db.Query<Seller>().Where(s => s.Id == sellerId)) ?? throw new SellerAccessDeniedException();
        if (seller.Status == SellerStatus.Closed) return false;

        var application = await db.First(db.Query<StoreApplication>().Where(a => a.SellerId == sellerId));
        if (application is not null)
        {
            var verification = await db.First(db.Query<StoreApplicationVerification>().Where(a => a.ApplicationId == application.Id));
            if (verification is not null) db.Remove(verification);
        }

        seller.Status = SellerStatus.Closed;
        db.Audit(actor.UserId, "Close store", sellerId.ToString(System.Globalization.CultureInfo.InvariantCulture),
            time.GetUtcNow(), sellerId: sellerId);
        return true;
    });

    private static void RequireReviewer(ShopActor actor)
    {
        actor.RequireAdmin();
        if (!actor.Mfa) throw new SellerAccessDeniedException();
    }

    private static IQueryable<StoreApplicationSummary> Summaries(IQueryable<StoreApplication> query) =>
        query.Select(a => new StoreApplicationSummary(a.Id, a.StoreName, a.Email, a.Status, a.SubmittedAt,
            a.DecidedAt, a.SellerId, a.DeclineReason, a.ReadAt));
}
