using HKShop.Application;
using HKShop.Application.Checkout;
using HKShop.Contracts.Clients;
using HKShop.Domain;
using HKShop.Domain.Orders;
using HKShop.Messaging;
using HKShop.Ordering.Api.Domain;
using HKShop.Ordering.Api.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace HKShop.Ordering.Api.Application;

public sealed class OrderingCart(IDbContextFactory<OrderingDbContext> factory, CatalogServiceClient catalog, TimeProvider time)
{
    public async Task<CartView> ReadAsync(string? customer, string? guest, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var cart = await FindAsync(db, customer, guest, ct);
        if (cart is null) return new(Guid.Empty, []);
        var offers = (await catalog.CartOffersAsync(new(cart.Items.Select(i => i.OfferId).ToList()), ct))
            .Offers.ToDictionary(o => o.OfferId);
        var lines = cart.Items.Select(i => offers.TryGetValue(i.OfferId, out var offer)
            ? offer with { Quantity = i.Quantity, PriceChanged = i.SeenUnitPriceVnd.HasValue && offer.Price != i.SeenUnitPriceVnd }
            : new CartLine(i.OfferId, i.SkuId, 0, "", "", "", "Unavailable item", i.Quantity, 0, i.SeenUnitPriceVnd ?? 0, false, false))
            .OrderBy(l => l.SellerId).ThenBy(l => l.SkuId).ThenBy(l => l.OfferId);
        return new(cart.Revision, lines.ToList());
    }

    public async Task<int> ChangeAsync(string? customer, CartChangeRequest body, CancellationToken ct)
    {
        if (body.OfferId.HasValue == body.SkuId.HasValue || body.OfferId is <= 0 || body.SkuId is <= 0 ||
            body.Quantity is < 0 or > 999 || (body.Add && body.Quantity == 0))
            throw new ShopRuleException("Invalid cart item or quantity.");
        var offer = (await catalog.CartOffersAsync(new(body.OfferId is long id ? [id] : [], body.SkuId), ct)).Offers.SingleOrDefault();
        return await SerializableTransaction.ExecuteAsync(factory, async db =>
        {
            var cart = await GetOrCreateAsync(db, customer, body.GuestHash, ct);
            var item = body.OfferId.HasValue
                ? cart.Items.SingleOrDefault(i => i.OfferId == body.OfferId)
                : (offer is not null
                    ? cart.Items.SingleOrDefault(i => i.OfferId == offer.OfferId)
                    : cart.Items.FirstOrDefault(i => i.SkuId == body.SkuId));
            var next = body.Add ? (item?.Quantity ?? 0) + body.Quantity : body.Quantity;
            if (next == 0)
            {
                if (item is not null) cart.Items.Remove(item);
            }
            else
            {
                if (offer is null || !offer.Active || next > Math.Min(offer.Available, 999))
                    throw new ShopRuleException("The requested quantity exceeds the available stock.");
                SetItem(cart, item, offer, next);
            }
            Touch(cart);
            return next;
        }, ct);
    }

    public Task<int> MergeAsync(string customer, string guest, CancellationToken ct) =>
        SerializableTransaction.ExecuteAsync(factory, async db =>
        {
            var other = await FindAsync(db, null, guest, ct);
            if (other is null) return 0;
            var cart = await GetOrCreateAsync(db, customer, null, ct);
            var adjusted = await AddAvailableAsync(cart, other.Items.Select(i => (i.OfferId, i.Quantity)).ToList(), ct);
            db.ShoppingCarts.Remove(other);
            return adjusted;
        }, ct);

    public Task<int> BuyAgainAsync(string customer, Guid orderId, CancellationToken ct) =>
        SerializableTransaction.ExecuteAsync(factory, async db =>
        {
            var order = await db.Orders.Include(o => o.Items).SingleOrDefaultAsync(o => o.Id == orderId && o.CustomerId == customer, ct)
                ?? throw new ShopRuleException("Order not found.");
            order.RequireStatus(OrderStatus.PaymentFailed, OrderStatus.Expired, OrderStatus.Cancelled);
            var cart = await GetOrCreateAsync(db, customer, null, ct);
            return await AddAvailableAsync(cart, order.Items.Select(i => (i.OfferId, i.Quantity)).ToList(), ct);
        }, ct);

    private async Task<int> AddAvailableAsync(ShoppingCart cart, List<(long OfferId, int Quantity)> items, CancellationToken ct)
    {
        var offers = (await catalog.CartOffersAsync(new(items.Select(i => i.OfferId).ToList()), ct)).Offers.ToDictionary(o => o.OfferId);
        var adjusted = 0;
        foreach (var line in items)
        {
            var item = cart.Items.SingleOrDefault(i => i.OfferId == line.OfferId);
            var requested = (item?.Quantity ?? 0) + line.Quantity;
            offers.TryGetValue(line.OfferId, out var offer);
            var available = offer is { Active: true } ? offer.Available : 0;
            var quantity = Math.Min(requested, Math.Min(available, 999));
            if (quantity < requested) adjusted++;
            if (quantity > 0) SetItem(cart, item, offer!, quantity);
            else if (item is not null) cart.Items.Remove(item);
        }
        Touch(cart);
        return adjusted;
    }

    private static void SetItem(ShoppingCart cart, CartItem? item, CartLine offer, int quantity)
    {
        if (item is null)
        {
            item = new CartItem { OfferId = offer.OfferId, SkuId = offer.SkuId };
            cart.Items.Add(item);
        }
        item.SeenUnitPriceVnd = offer.Price;
        item.Quantity = quantity;
    }

    private Task<ShoppingCart?> FindAsync(OrderingDbContext db, string? customer, string? guest, CancellationToken ct)
    {
        if (customer is null && guest is null) return Task.FromResult<ShoppingCart?>(null);
        if (customer is null && (guest!.Length != 64 || !guest.All(char.IsAsciiHexDigit)))
            throw new ShopRuleException("Invalid guest cart.");
        var cutoff = time.GetUtcNow().AddDays(-30);
        return db.ShoppingCarts.Include(c => c.Items).SingleOrDefaultAsync(c => customer != null
            ? c.CustomerId == customer : c.GuestTokenHash == guest && c.UpdatedAt >= cutoff, ct);
    }

    private async Task<ShoppingCart> GetOrCreateAsync(OrderingDbContext db, string? customer, string? guest, CancellationToken ct)
    {
        if (customer is null && guest is null) throw new ShopRuleException("Please enable cookies to use the shopping cart.");
        var cart = await FindAsync(db, customer, guest, ct);
        if (cart is not null) return cart;
        if (customer is null)
            await db.ShoppingCarts.Where(c => c.GuestTokenHash == guest).ExecuteDeleteAsync(ct);
        cart = new ShoppingCart { CustomerId = customer, GuestTokenHash = customer is null ? guest : null };
        db.ShoppingCarts.Add(cart);
        Touch(cart);
        return cart;
    }

    private void Touch(ShoppingCart cart)
    {
        cart.Revision = Guid.NewGuid();
        cart.UpdatedAt = time.GetUtcNow();
    }
}
