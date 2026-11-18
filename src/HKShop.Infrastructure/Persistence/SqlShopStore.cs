using System.Data;
using HKShop.Application;
using HKShop.Domain;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;

namespace HKShop.Infrastructure.Persistence;

public sealed class SqlShopStore(IDbContextFactory<ShopDbContext> factory) : IShopStore
{
    public async Task<T> Read<T>(Func<IShopSession, Task<T>> query)
    {
        await using var context = await factory.CreateDbContextAsync();
        context.ChangeTracker.QueryTrackingBehavior = QueryTrackingBehavior.NoTracking;
        return await query(new Session(context));
    }

    public async Task<T> Write<T>(Func<IShopSession, Task<T>> action)
    {
        for (var attempt = 1; ; attempt++)
        {
            await using var context = await factory.CreateDbContextAsync();
            await using var transaction = await context.Database.BeginTransactionAsync(IsolationLevel.Serializable);
            try
            {
                var result = await action(new Session(context));
                await context.SaveChangesAsync();
                await transaction.CommitAsync();
                return result;
            }
            catch (Exception exception) when (attempt < 3 && IsRetryable(exception))
            {
                // Disposing rolls back the transaction before the next attempt reads current data.
            }
            catch (DbUpdateException exception) when (exception.InnerException is SqlException { Number: 2601 or 2627 })
            {
                throw new ShopRuleException("The data already exists or has changed. Please reload.");
            }
            catch (Exception exception) when (IsRetryable(exception))
            {
                throw new ShopRuleException("The data has changed. Please reload and try again.");
            }
        }
    }

    private static bool IsRetryable(Exception exception)
    {
        for (Exception? cause = exception; cause is not null; cause = cause.InnerException)
            if (cause is DbUpdateConcurrencyException or SqlException { Number: 1205 }) return true;

        return false;
    }

    private sealed class Session(ShopDbContext context) : IShopSession
    {
        public IQueryable<T> Query<T>() where T : class => context.Set<T>();
        public Task<List<T>> List<T>(IQueryable<T> query) => query.ToListAsync();
        public Task<T?> First<T>(IQueryable<T> query) => query.FirstOrDefaultAsync();
        public Task<int> Count<T>(IQueryable<T> query) => query.CountAsync();
        public Task<decimal> Sum(IQueryable<decimal> query) => query.SumAsync();
        public void Add<T>(T entity) where T : class => context.Add(entity);
        public void Remove<T>(T entity) where T : class => context.Remove(entity);
        public async Task Save() => await context.SaveChangesAsync();
    }
}
