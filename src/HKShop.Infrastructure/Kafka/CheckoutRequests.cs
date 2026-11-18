using System.Text.Json;
using HKShop.Application;
using HKShop.Application.Checkout;
using HKShop.Contracts.Ports;
using HKShop.Domain;
using HKShop.Domain.Orders;
using HKShop.Infrastructure.Persistence;
using Microsoft.AspNetCore.Identity;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;

namespace HKShop.Infrastructure.Kafka;

public sealed class CheckoutRequest
{
    public Guid Id { get; set; }
    public string CustomerId { get; set; } = "";
    public string QuoteJson { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
    public DateTimeOffset? PublishedAt { get; set; }
    public CheckoutRequestStatus Status { get; set; }
    public Guid? OrderId { get; set; }
    public string? Error { get; set; }
}

public sealed class CheckoutRequests(IShopStore store, CheckoutService checkout,
    IDbContextFactory<ShopDbContext> factory, TimeProvider time) : ICheckoutRequestDispatcher
{
    private sealed class CheckoutRejected(string message) : Exception(message);

    public async Task<CheckoutResult> Submit(ShopActor actor, CheckoutQuote quote)
    {
        CheckoutService.ValidateSubmission(actor, quote);
        await using var db = await factory.CreateDbContextAsync();
        var request = await db.Set<CheckoutRequest>().AsNoTracking()
            .FirstOrDefaultAsync(r => r.Id == quote.SubmissionKey && r.CustomerId == actor.UserId);
        if (request is not null) return Result(request);

        var orderId = await db.Set<Order>().Where(o => o.CustomerId == actor.UserId && o.SubmissionKey == quote.SubmissionKey)
            .Select(o => (Guid?)o.Id).FirstOrDefaultAsync();
        if (orderId is not null)
            return new CheckoutResult(quote.SubmissionKey, CheckoutRequestStatus.Placed, orderId, null);

        var now = time.GetUtcNow();
        if (quote.ExpiresAt <= now) throw new ShopRuleException(CheckoutService.ExpiredQuoteMessage);

        request = new CheckoutRequest
        {
            Id = quote.SubmissionKey, CustomerId = actor.UserId, QuoteJson = JsonSerializer.Serialize(quote),
            CreatedAt = now, ExpiresAt = quote.ExpiresAt
        };
        db.Add(request);
        try
        {
            await db.SaveChangesAsync();
            return Result(request);
        }
        catch (DbUpdateException error) when (error.InnerException is SqlException { Number: 2601 or 2627 })
        {
            return await Get(actor, quote.SubmissionKey) ?? throw new ShopRuleException("Invalid checkout session.");
        }
    }

    public Task<CheckoutResult?> Get(ShopActor actor, Guid id)
    {
        if (!actor.Verified || string.IsNullOrEmpty(actor.UserId)) return Task.FromResult<CheckoutResult?>(null);
        return store.Read(async db =>
        {
            var request = await db.First(db.Query<CheckoutRequest>()
                .Where(r => r.Id == id && r.CustomerId == actor.UserId));
            return request is null ? null : Result(request);
        });
    }

    public async Task Process(Guid id)
    {
        try
        {
            await store.Write(async db =>
            {
                var request = await db.First(db.Query<CheckoutRequest>().Where(r => r.Id == id));
                if (request is null || request.Status != CheckoutRequestStatus.Pending) return false;

                try
                {
                    var user = await db.First(db.Query<ShopUser>()
                        .Where(u => u.Id == request.CustomerId && u.EmailConfirmed && !u.Disabled));
                    if (user is null) throw new ShopRuleException("This account cannot place an order.");
                    var hasEmployeeRole = await db.Count(
                        from userRole in db.Query<IdentityUserRole<string>>()
                        join role in db.Query<IdentityRole>() on userRole.RoleId equals role.Id
                        where userRole.UserId == user.Id &&
                              (role.Name == "Admin" || role.Name == "StoreStaff" || role.Name == "Shipper")
                        select userRole) != 0;
                    if (hasEmployeeRole)
                        throw new ShopRuleException("Employee and shipper accounts cannot use shopping features.");
                    var actor = new ShopActor(user.Id, user.Email ?? "", true, false, false, false);
                    var quote = JsonSerializer.Deserialize<CheckoutQuote>(request.QuoteJson)
                        ?? throw new InvalidOperationException("Missing checkout quote.");
                    request.OrderId = await checkout.PlaceInTransaction(db, actor, quote);
                    request.Status = CheckoutRequestStatus.Placed;
                    return true;
                }
                catch (ShopRuleException error)
                {
                    // Roll back before recording the business rejection. Store-level SQL errors remain retryable.
                    throw new CheckoutRejected(error.Message);
                }
            });
        }
        catch (CheckoutRejected error)
        {
            await using var db = await factory.CreateDbContextAsync();
            await db.Set<CheckoutRequest>().Where(r => r.Id == id && r.Status == CheckoutRequestStatus.Pending)
                .ExecuteUpdateAsync(update => update
                    .SetProperty(r => r.Status, CheckoutRequestStatus.Rejected)
                    .SetProperty(r => r.Error, error.Message));
        }
    }

    public async Task<int> Expire(CancellationToken cancellationToken)
    {
        var now = time.GetUtcNow();
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        return await db.Set<CheckoutRequest>()
            .Where(r => r.Status == CheckoutRequestStatus.Pending && r.ExpiresAt <= now)
            .ExecuteUpdateAsync(update => update
                .SetProperty(r => r.Status, CheckoutRequestStatus.Rejected)
                .SetProperty(r => r.Error, CheckoutService.ExpiredQuoteMessage), cancellationToken);
    }

    private static CheckoutResult Result(CheckoutRequest request) =>
        new(request.Id, request.Status, request.OrderId, request.Error);
}
