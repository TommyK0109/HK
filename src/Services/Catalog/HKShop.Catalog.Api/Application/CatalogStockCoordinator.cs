using System.Data;
using System.Globalization;
using System.Text.Json;
using HKShop.Application;
using HKShop.Application.Catalog;
using HKShop.Application.Sellers;
using HKShop.Catalog.Api.Domain;
using HKShop.Catalog.Api.Infrastructure;
using HKShop.Contracts.Clients;
using HKShop.Contracts.Events;
using HKShop.Contracts.Ports;
using HKShop.Domain;
using HKShop.Domain.Catalog;
using HKShop.Domain.Orders;
using HKShop.Domain.Sellers;
using HKShop.Messaging;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;

namespace HKShop.Catalog.Api.Application;

public sealed class CatalogStockCoordinator(
    IDbContextFactory<CatalogDbContext> factory,
    IProductImageStore media,
    IDataProtectionProvider protection,
    TimeProvider time)
{
    private readonly IDataProtector verifierProtector = protection.CreateProtector("HKShop.StoreApplicationVerification.v1");

    public async Task<IReadOnlyList<QuotedOfferDto>> QuoteOffersAsync(IReadOnlyList<long> offerIds, CancellationToken ct)
    {
        if (offerIds.Count == 0) return [];
        await using var db = await factory.CreateDbContextAsync(ct);

        var rows = await (
            from offer in db.SellerOffers.AsNoTracking()
            join sku in db.Skus.AsNoTracking() on offer.SkuId equals sku.Id
            join product in db.Products.AsNoTracking() on sku.ProductId equals product.Id
            join category in db.Categories.AsNoTracking() on product.CategoryId equals (long?)category.Id
            join seller in db.Sellers.AsNoTracking() on offer.SellerId equals seller.Id
            join balance in db.InventoryBalances.AsNoTracking()
                on new { OfferId = offer.Id, offer.SellerId } equals new { balance.OfferId, balance.SellerId }
            join location in db.SellerLocations.AsNoTracking()
                on new { Id = balance.LocationId, balance.SellerId } equals new { location.Id, location.SellerId }
            where offerIds.Contains(offer.Id)
            select new
            {
                offer,
                sku,
                product,
                categoryActive = category.Active,
                seller,
                balance,
                location
            }).ToListAsync(ct);

        return rows.Select(r =>
        {
            var active = r.offer.Status == SellerOfferStatus.Approved &&
                         r.offer.PriceVnd is > 0 &&
                         r.sku.Active &&
                         r.product.Status == ProductStatus.Published &&
                         r.categoryActive &&
                         r.seller.Status == SellerStatus.Active &&
                         r.location.Active &&
                         r.product.SellerId == r.offer.SellerId;
            var snapshot = string.Join(" / ", new[] { r.product.Name, r.sku.Size, r.sku.Color }.Where(s => !string.IsNullOrWhiteSpace(s)));
            if (snapshot.Length > 300) snapshot = snapshot[..300];

            return new QuotedOfferDto(
                r.offer.Id,
                r.sku.Id,
                r.seller.Id,
                r.seller.DisplayName,
                r.sku.Code,
                r.offer.SellerSkuCode,
                r.product.Slug,
                r.product.Name,
                snapshot,
                r.offer.PriceVnd ?? 0m,
                Math.Max(0, r.balance.OnHand - r.balance.Reserved),
                active,
                r.location.Id,
                r.location.PickupAddress,
                r.location.Latitude,
                r.location.Longitude,
                r.product.Id);
        }).ToList();
    }

    public Task<ReserveStockResponse> ReserveOffersAsync(Guid orderId, IReadOnlyList<StockReservationLine> lines, CancellationToken ct)
    {
        if (orderId == Guid.Empty || lines.Count == 0)
            return Task.FromResult(new ReserveStockResponse(false, "Order reservation requires at least one line."));

        return SerializableTransaction.ExecuteAsync(factory, db => ReserveInContextAsync(db, orderId, lines, ct), ct);
    }

    internal async Task<ReserveStockResponse> ReserveInContextAsync(
        CatalogDbContext db, Guid orderId, IReadOnlyList<StockReservationLine> lines, CancellationToken ct)
    {
        var rejectionKey = $"StockReservationFailed:{orderId:N}";
        var rejection = await db.Set<ServiceOutboxEvent>().AsNoTracking()
            .Where(e => e.IdempotencyKey == rejectionKey).Select(e => e.PayloadJson).FirstOrDefaultAsync(ct);
        if (rejection is not null)
            return new(false, JsonSerializer.Deserialize<StockReservationFailed>(rejection)!.Reason);

        var existingHolds = await db.StockHolds.Where(h => h.OrderId == orderId).ToListAsync(ct);
        if (existingHolds.Count > 0)
        {
            var alreadyHeld = existingHolds.All(h => h.Status is ReservationStatus.Held or ReservationStatus.Consumed);
            return new ReserveStockResponse(alreadyHeld, alreadyHeld ? null : "Order stock reservation was previously released.");
        }

        var now = time.GetUtcNow();
        var offerIds = lines.Select(l => l.OfferId).Distinct().ToArray();
        var offers = await (
            from offer in db.SellerOffers
            join sku in db.Skus on offer.SkuId equals sku.Id
            join product in db.Products on sku.ProductId equals product.Id
            join category in db.Categories on product.CategoryId equals (long?)category.Id
            join seller in db.Sellers on offer.SellerId equals seller.Id
            join balance in db.InventoryBalances on new { OfferId = offer.Id, offer.SellerId } equals new { balance.OfferId, balance.SellerId }
            join location in db.SellerLocations on new { Id = balance.LocationId, balance.SellerId } equals new { location.Id, location.SellerId }
            where offerIds.Contains(offer.Id) && location.Active
            orderby balance.Id
            select new { offer, sku, product, category, seller, balance })
            .ToListAsync(ct);

        var byOffer = offers.ToDictionary(x => x.offer.Id);
        if (lines.Count == 0 || offerIds.Length != lines.Count)
            return RejectReservation(db, orderId, "Order lines must identify distinct offers.", now);
        foreach (var line in lines.OrderBy(l => l.OfferId))
        {
            if (line.Quantity <= 0 || !byOffer.TryGetValue(line.OfferId, out var entry))
                return RejectReservation(db, orderId, "One or more offers are unavailable.", now);

            if (entry.offer.SellerId != line.SellerId || entry.sku.Id != line.SkuId ||
                entry.offer.Status != SellerOfferStatus.Approved ||
                entry.seller.Status != SellerStatus.Active ||
                !entry.sku.Active ||
                entry.product.Status != ProductStatus.Published ||
                !entry.category.Active ||
                entry.offer.PriceVnd != line.ExpectedUnitPriceVnd)
                return RejectReservation(db, orderId, "Pricing or offer availability changed.", now);

            if (line.Quantity > entry.balance.Available)
                return RejectReservation(db, orderId, "Insufficient stock for one or more items.", now);
        }

        foreach (var line in lines.OrderBy(l => byOffer[l.OfferId].balance.Id))
        {
            var entry = byOffer[line.OfferId];
            entry.balance.Reserve(line.Quantity);
            db.OfferStockMovements.Add(new OfferStockMovement
            {
                InventoryBalanceId = entry.balance.Id,
                OnHandDelta = 0,
                ReservedDelta = line.Quantity,
                Reason = "Order reservation",
                OccurredAt = now
            });

            if (entry.balance.SellerId == Seller.FirstPartyId)
            {
                var legacy = await db.StockBalances.FirstOrDefaultAsync(s => s.SkuId == entry.sku.Id, ct);
                if (legacy is not null)
                {
                    legacy.Reserve(line.Quantity);
                    db.StockMovements.Add(new StockMovement
                    {
                        SkuId = entry.sku.Id,
                        OrderId = orderId,
                        OnHandDelta = 0,
                        ReservedDelta = line.Quantity,
                        Reason = "Order reservation",
                        OccurredAt = now
                    });
                }
            }

            db.StockHolds.Add(new CatalogStockHold
            {
                OrderId = orderId,
                OfferId = line.OfferId,
                SellerOrderId = line.SellerOrderId,
                SkuId = entry.sku.Id,
                SellerId = entry.seller.Id,
                InventoryBalanceId = entry.balance.Id,
                Quantity = line.Quantity,
                Status = ReservationStatus.Held,
                CreatedAt = now
            });
        }

        db.EnqueueOutboxEvent("Order", orderId.ToString("D"), nameof(StockReserved),
            new StockReserved(orderId, lines, now), $"StockReserved:{orderId:N}", now);

        return new ReserveStockResponse(true, null);
    }

    private static ReserveStockResponse RejectReservation(
        CatalogDbContext db,
        Guid orderId,
        string reason,
        DateTimeOffset now)
    {
        db.EnqueueOutboxEvent("Order", orderId.ToString("D"), nameof(StockReservationFailed),
            new StockReservationFailed(orderId, reason, now), $"StockReservationFailed:{orderId:N}", now);
        return new ReserveStockResponse(false, reason);
    }

    public async Task ReleaseOrderStockAsync(Guid orderId, Guid? sellerOrderId, string reason, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        await ReleaseInContextAsync(db, orderId, sellerOrderId, reason, time.GetUtcNow(), ct);
        await db.SaveChangesAsync(ct);
    }

    public static async Task ReleaseInContextAsync(
        CatalogDbContext db,
        Guid orderId,
        Guid? sellerOrderId,
        string reason,
        DateTimeOffset now,
        CancellationToken ct)
    {
        var holds = await db.StockHolds
            .Where(h => h.OrderId == orderId && h.Status == ReservationStatus.Held &&
                (sellerOrderId == null || h.SellerOrderId == sellerOrderId))
            .OrderBy(h => h.InventoryBalanceId)
            .ToListAsync(ct);

        if (holds.Count == 0) return;

        foreach (var hold in holds)
        {
            var balance = await db.InventoryBalances.FirstOrDefaultAsync(b => b.Id == hold.InventoryBalanceId, ct);
            if (balance is not null)
            {
                balance.Release(hold.Quantity);
                db.OfferStockMovements.Add(new OfferStockMovement
                {
                    InventoryBalanceId = balance.Id,
                    OnHandDelta = 0,
                    ReservedDelta = -hold.Quantity,
                    Reason = reason,
                    OccurredAt = now
                });
            }

            if (hold.SellerId == Seller.FirstPartyId)
            {
                var legacy = await db.StockBalances.FirstOrDefaultAsync(s => s.SkuId == hold.SkuId, ct);
                if (legacy is not null)
                {
                    legacy.Release(hold.Quantity);
                    db.StockMovements.Add(new StockMovement
                    {
                        SkuId = hold.SkuId,
                        OrderId = orderId,
                        OnHandDelta = 0,
                        ReservedDelta = -hold.Quantity,
                        Reason = reason,
                        OccurredAt = now
                    });
                }
            }

            hold.Status = ReservationStatus.Released;
        }

        db.EnqueueOutboxEvent("Order", orderId.ToString("D"), nameof(StockReleased),
            new StockReleased(orderId, sellerOrderId, reason, now),
            sellerOrderId is Guid child ? $"StockReleased:{orderId:N}:{child:N}" : $"StockReleased:{orderId:N}", now);
    }

    public async Task<IReadOnlyList<CatalogCategoryDto>> CategoriesAsync(bool activeOnly, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        return await db.Categories.AsNoTracking()
            .Where(c => !activeOnly || c.Active)
            .OrderBy(c => c.Name)
            .Select(c => new CatalogCategoryDto(c.Id, c.Name, c.Active))
            .ToListAsync(ct);
    }

    public async Task<Page<CatalogProductDto>> ProductsAsync(ShopActor actor, int page, CancellationToken ct)
    {
        actor.RequireStaff();
        await using var db = await factory.CreateDbContextAsync(ct);
        var total = await db.Products.CountAsync(ct);
        var number = Math.Clamp(page, 1, Math.Max(1, (int)Math.Ceiling(total / 25d)));
        var items = await db.Products.AsNoTracking()
            .OrderByDescending(p => p.Id)
            .Skip((number - 1) * 25)
            .Take(25)
            .Select(p => new CatalogProductDto(p.Id, p.CategoryId, p.Name, p.Slug, p.Description, p.LegacyPrice, p.Status))
            .ToListAsync(ct);
        return new Page<CatalogProductDto>(items, number, total, 25);
    }

    public async Task<CatalogEditDetailsDto> EditDetailsAsync(ShopActor actor, int id, CancellationToken ct)
    {
        actor.RequireStaff();
        await using var db = await factory.CreateDbContextAsync(ct);
        var product = await db.Products.AsNoTracking().FirstOrDefaultAsync(p => p.Id == id, ct)
            ?? throw new ShopRuleException("The product does not exist.");

        var offerBalances = from offer in db.SellerOffers.AsNoTracking()
                            join sku in db.Skus.AsNoTracking() on offer.SkuId equals sku.Id
                            join prod in db.Products.AsNoTracking() on sku.ProductId equals prod.Id
                            join seller in db.Sellers.AsNoTracking() on offer.SellerId equals seller.Id
                            join balance in db.InventoryBalances.AsNoTracking() on new { OfferId = offer.Id, offer.SellerId } equals new { balance.OfferId, balance.SellerId }
                            join location in db.SellerLocations.AsNoTracking() on new { Id = balance.LocationId, balance.SellerId } equals new { location.Id, location.SellerId }
                            where offer.Status == SellerOfferStatus.Approved && seller.Status == SellerStatus.Active && location.Active && prod.SellerId == offer.SellerId
                            select new { offer.SkuId, balance.OnHand, balance.Reserved };

        var variants = await (
            from sku in db.Skus.AsNoTracking()
            join stock in db.StockBalances.AsNoTracking() on sku.Id equals stock.SkuId
            where sku.ProductId == id
            orderby sku.Id
            select new SkuDisplay(
                sku.Id, sku.Code, sku.Size, sku.Color, sku.PriceVnd, sku.Active,
                stock.OnHand - stock.Reserved,
                offerBalances.Where(b => b.SkuId == sku.Id).Sum(b => (int?)b.OnHand) ?? 0,
                offerBalances.Where(b => b.SkuId == sku.Id).Sum(b => (int?)(b.OnHand - b.Reserved)) ?? 0))
            .ToListAsync(ct);

        var images = await db.ProductImages.AsNoTracking()
            .Where(i => i.ProductId == id)
            .OrderBy(i => i.Id)
            .Select(i => new CatalogImageDto(i.Id, i.ProductId, i.FileName, i.Alt))
            .ToListAsync(ct);

        return new CatalogEditDetailsDto(
            new CatalogProductDto(product.Id, product.CategoryId, product.Name, product.Slug, product.Description, product.LegacyPrice, product.Status),
            variants,
            images);
    }

    public async Task<int> SaveCategoryAsync(ShopActor actor, long id, string name, bool active, CancellationToken ct)
    {
        actor.RequireStaff();
        name = ShopRules.Text(name, 100, "Category");
        await using var db = await factory.CreateDbContextAsync(ct);
        var row = id == 0
            ? new Category()
            : await db.Categories.FirstOrDefaultAsync(c => c.Id == id, ct) ?? throw new ShopRuleException("The category does not exist.");
        if (await db.Categories.AnyAsync(c => c.Name == name && c.Id != id, ct))
            throw new ShopRuleException("The category already exists.");
        row.Name = name;
        row.Active = active;
        if (id == 0) db.Categories.Add(row);
        await db.SaveChangesAsync(ct);
        db.AddAuditEntry(actor.UserId, "Danh mục", row.Id.ToString(CultureInfo.InvariantCulture), time.GetUtcNow());
        await db.SaveChangesAsync(ct);
        return (int)row.Id;
    }

    public async Task<int> SaveProductAsync(ShopActor actor, int id, string name, string slug, string description, long? categoryId, CancellationToken ct)
    {
        actor.RequireStaff();
        name = ShopRules.Text(name, 150, "Product name");
        slug = ShopRules.Text(slug, 150, "Slug").ToLowerInvariant();
        description = ShopRules.Text(description, 4000, "Description", false);
        if (slug.Any(c => !(char.IsAsciiLetterOrDigit(c) || c == '-')))
            throw new ShopRuleException("The slug may contain only Latin letters, digits, and hyphens.");

        await using var db = await factory.CreateDbContextAsync(ct);
        var row = id == 0
            ? new Product()
            : await db.Products.FirstOrDefaultAsync(p => p.Id == id, ct) ?? throw new ShopRuleException("The product does not exist.");
        if (await db.Products.AnyAsync(p => p.Slug == slug && p.Id != id, ct))
            throw new ShopRuleException("The product slug already exists.");
        if (categoryId is not null && !await db.Categories.AnyAsync(c => c.Id == categoryId, ct))
            throw new ShopRuleException("The category does not exist.");
        if (row.Status == ProductStatus.Published && !await db.Categories.AnyAsync(c => c.Id == categoryId && c.Active, ct))
            throw new ShopRuleException("A published product requires an active category.");

        row.Name = name;
        row.Slug = slug;
        row.Description = description;
        row.CategoryId = categoryId;
        if (id == 0) db.Products.Add(row);
        await db.SaveChangesAsync(ct);
        db.AddAuditEntry(actor.UserId, "Sản phẩm", row.Id.ToString(CultureInfo.InvariantCulture), time.GetUtcNow());
        await db.SaveChangesAsync(ct);
        return row.Id;
    }

    public async Task<long> SaveSkuAsync(ShopActor actor, int productId, long skuId, string code, string size, string color, decimal price, bool active, CancellationToken ct)
    {
        actor.RequireStaff();
        code = ShopRules.Text(code, 64, "SKU code").ToUpperInvariant();
        size = ShopRules.Text(size, 40, "Size", false);
        color = ShopRules.Text(color, 40, "Color", false);
        ShopRules.Vnd(price);

        await using var db = await factory.CreateDbContextAsync(ct);
        var product = await db.Products.FirstOrDefaultAsync(p => p.Id == productId, ct)
            ?? throw new ShopRuleException("The product does not exist.");
        var sku = skuId == 0
            ? new Sku { ProductId = productId }
            : await db.Skus.FirstOrDefaultAsync(s => s.Id == skuId && s.ProductId == productId, ct)
              ?? throw new ShopRuleException("The SKU does not exist.");
        if (await db.Skus.AnyAsync(s => s.Code == code && s.Id != skuId, ct))
            throw new ShopRuleException("The SKU code already exists.");
        if (product.Status == ProductStatus.Published && !active &&
            !await db.Skus.AnyAsync(s => s.ProductId == productId && s.Id != skuId && s.Active, ct))
            throw new ShopRuleException("Unpublish the product before deactivating its last SKU.");

        sku.Code = code;
        sku.Size = size;
        sku.Color = color;
        sku.PriceVnd = price;
        sku.Active = active;
        if (skuId == 0) db.Skus.Add(sku);
        await db.SaveChangesAsync(ct);

        if (skuId == 0 && product.SellerId == Seller.FirstPartyId)
        {
            var offer = new SellerOffer
            {
                SellerId = Seller.FirstPartyId,
                SkuId = sku.Id,
                SellerSkuCode = sku.Code,
                PriceVnd = price,
                Status = SellerOfferStatus.Approved
            };
            db.SellerOffers.Add(offer);
            await db.SaveChangesAsync(ct);
            db.InventoryBalances.Add(new InventoryBalance
            {
                SellerId = Seller.FirstPartyId,
                OfferId = offer.Id,
                LocationId = SellerLocation.FirstPartyId
            });
            await db.SaveChangesAsync(ct);
        }

        db.AddAuditEntry(actor.UserId, "Biến thể", sku.Id.ToString(CultureInfo.InvariantCulture), time.GetUtcNow());
        await db.SaveChangesAsync(ct);
        return sku.Id;
    }

    public async Task AddImageAsync(ShopActor actor, int productId, string alt, Stream file, CancellationToken ct)
    {
        actor.RequireStaff();
        alt = ShopRules.Text(alt, 200, "Image description");
        await using (var check = await factory.CreateDbContextAsync(ct))
        {
            if (!await check.Products.AnyAsync(p => p.Id == productId, ct))
                throw new ShopRuleException("The product does not exist.");
        }

        var name = await media.Save(file);
        try
        {
            await using var db = await factory.CreateDbContextAsync(ct);
            db.ProductImages.Add(new ProductImage { ProductId = productId, FileName = name, Alt = alt });
            db.AddAuditEntry(actor.UserId, "Ảnh sản phẩm", productId.ToString(CultureInfo.InvariantCulture), time.GetUtcNow());
            await db.SaveChangesAsync(ct);
        }
        catch
        {
            try { await media.Delete(name); } catch { }
            throw;
        }
    }

    public async Task<int> ChangePublicationAsync(ShopActor actor, int id, bool publish, CancellationToken ct)
    {
        actor.RequireStaff();
        await using var db = await factory.CreateDbContextAsync(ct);
        var product = await db.Products.Include(p => p.Skus).Include(p => p.Images).FirstOrDefaultAsync(p => p.Id == id, ct)
            ?? throw new ShopRuleException("The product does not exist.");
        if (publish)
        {
            var categoryActive = await db.Categories.AnyAsync(c => c.Id == product.CategoryId && c.Active, ct);
            product.Publish(categoryActive);
        }
        else
        {
            product.Archive();
        }
        db.AddAuditEntry(actor.UserId, publish ? "Đăng bán" : "Ẩn sản phẩm", id.ToString(CultureInfo.InvariantCulture), time.GetUtcNow());
        await db.SaveChangesAsync(ct);
        return id;
    }

    public async Task<Page<StockRow>> ListFirstPartyStockAsync(ShopActor actor, bool low, int page, CancellationToken ct)
    {
        actor.RequireStaff();
        await using var db = await factory.CreateDbContextAsync(ct);
        var offerBalances = from offer in db.SellerOffers.AsNoTracking()
                            join sku in db.Skus.AsNoTracking() on offer.SkuId equals sku.Id
                            join prod in db.Products.AsNoTracking() on sku.ProductId equals prod.Id
                            join seller in db.Sellers.AsNoTracking() on offer.SellerId equals seller.Id
                            join balance in db.InventoryBalances.AsNoTracking() on new { OfferId = offer.Id, offer.SellerId } equals new { balance.OfferId, balance.SellerId }
                            join location in db.SellerLocations.AsNoTracking() on new { Id = balance.LocationId, balance.SellerId } equals new { location.Id, location.SellerId }
                            where offer.Status == SellerOfferStatus.Approved && seller.Status == SellerStatus.Active && location.Active && prod.SellerId == offer.SellerId
                            select new { offer.SkuId, balance.OnHand, balance.Reserved };

        var query = from sku in db.Skus.AsNoTracking()
                    join product in db.Products.AsNoTracking() on sku.ProductId equals product.Id
                    join stock in db.StockBalances.AsNoTracking() on sku.Id equals stock.SkuId
                    where product.SellerId == Seller.FirstPartyId &&
                          (!low || (sku.Active && stock.OnHand - stock.Reserved <= stock.LowThreshold))
                    orderby sku.Code
                    select new StockRow(
                        sku.Id, sku.Code, product.Name, stock.OnHand, stock.Reserved, stock.LowThreshold,
                        offerBalances.Where(b => b.SkuId == sku.Id).Sum(b => (int?)b.OnHand) ?? 0,
                        offerBalances.Where(b => b.SkuId == sku.Id).Sum(b => (int?)(b.OnHand - b.Reserved)) ?? 0);

        var count = await query.CountAsync(ct);
        var number = Math.Clamp(page, 1, Math.Max(1, (int)Math.Ceiling(count / 25d)));
        return new Page<StockRow>(await query.Skip((number - 1) * 25).Take(25).ToListAsync(ct), number, count, 25);
    }

    public async Task<IReadOnlyList<StockMovementDto>> FirstPartyHistoryAsync(ShopActor actor, long skuId, CancellationToken ct)
    {
        actor.RequireStaff();
        await using var db = await factory.CreateDbContextAsync(ct);
        return await db.StockMovements.AsNoTracking()
            .Where(m => m.SkuId == skuId)
            .OrderByDescending(m => m.Id)
            .Take(100)
            .Select(m => new StockMovementDto(m.Id, m.SkuId, m.OrderId, m.OnHandDelta, m.ReservedDelta, m.Reason, m.ActorId, m.OccurredAt))
            .ToListAsync(ct);
    }

    public async Task<int> AdjustFirstPartyStockAsync(ShopActor actor, long skuId, int delta, int threshold, string reason, CancellationToken ct)
    {
        actor.RequireStaff();
        reason = ShopRules.Text(reason, 250, "Reason");
        if (threshold < 0) throw new ShopRuleException("The stock threshold must be zero or greater.");

        await using var db = await factory.CreateDbContextAsync(ct);
        var skuExists = await (from sku in db.Skus
                               join product in db.Products on sku.ProductId equals product.Id
                               where sku.Id == skuId && product.SellerId == Seller.FirstPartyId
                               select sku.Id).AnyAsync(ct);
        if (!skuExists) throw new ShopRuleException("This variant does not belong to HKShop.");

        var stock = await db.StockBalances.FirstOrDefaultAsync(s => s.SkuId == skuId, ct)
            ?? throw new ShopRuleException("Inventory was not found.");
        stock.Adjust(delta);
        stock.LowThreshold = threshold;
        var now = time.GetUtcNow();
        if (delta != 0)
        {
            db.StockMovements.Add(new StockMovement
            {
                SkuId = skuId,
                OnHandDelta = delta,
                ReservedDelta = 0,
                Reason = reason,
                ActorId = actor.UserId,
                OccurredAt = now
            });
        }

        var offerBalance = await (from balance in db.InventoryBalances
                                  join offer in db.SellerOffers on balance.OfferId equals offer.Id
                                  join location in db.SellerLocations on new { Id = balance.LocationId, balance.SellerId } equals new { location.Id, location.SellerId }
                                  where balance.SellerId == Seller.FirstPartyId && offer.SkuId == skuId && location.Active
                                  orderby balance.Id
                                  select balance).FirstOrDefaultAsync(ct);
        if (offerBalance is not null)
        {
            offerBalance.OnHand = stock.OnHand;
            offerBalance.Reserved = stock.Reserved;
            if (delta != 0)
            {
                db.OfferStockMovements.Add(new OfferStockMovement
                {
                    InventoryBalanceId = offerBalance.Id,
                    OnHandDelta = delta,
                    ReservedDelta = 0,
                    Reason = reason,
                    ActorId = actor.UserId,
                    OccurredAt = now
                });
            }
        }

        db.AddAuditEntry(actor.UserId, "Cập nhật kho", skuId.ToString(CultureInfo.InvariantCulture), now, reason);
        await db.SaveChangesAsync(ct);
        return stock.OnHand;
    }

    public async Task DecideStoreApplicationAsync(ShopActor reviewer, Guid id, bool approve, string? declineReason, CancellationToken ct)
    {
        reviewer.RequireAdmin();
        if (!reviewer.Mfa) throw new ShopRuleException("You cannot perform this action.");
        var reason = approve ? null : ShopRules.Text(declineReason, 250, "Decline reason");

        await using var db = await factory.CreateDbContextAsync(ct);
        var row = await db.StoreApplications.FirstOrDefaultAsync(a => a.Id == id, ct)
            ?? throw new ShopRuleException("The application was not found.");
        if (row.Status != StoreApplicationStatus.Pending)
            throw new ShopRuleException("The application has already been reviewed.");

        var now = time.GetUtcNow();
        if (approve)
        {
            var seller = new Seller
            {
                Slug = $"store-{Guid.NewGuid():N}",
                DisplayName = row.StoreName,
                CreatedAt = now,
                Status = SellerStatus.Active
            };
            db.Sellers.Add(seller);
            await db.SaveChangesAsync(ct);

            var membership = new SellerMembership
            {
                SellerId = seller.Id,
                UserId = row.UserId,
                Email = row.Email,
                Role = SellerRole.Owner,
                Status = SellerMembershipStatus.Active
            };
            db.SellerMemberships.Add(membership);
            row.SellerId = seller.Id;

            db.EnqueueOutboxEvent("Seller", seller.Id.ToString(CultureInfo.InvariantCulture), nameof(SellerStatusChanged),
                new SellerStatusChanged(seller.Id, seller.DisplayName, seller.Status, now),
                $"SellerStatusChanged:{seller.Id}:{seller.Status}", now);
            db.EnqueueOutboxEvent("SellerMembership", $"{seller.Id}:{row.UserId}", nameof(SellerMembershipChanged),
                new SellerMembershipChanged(seller.Id, seller.DisplayName, seller.Status, row.UserId, row.Email,
                    SellerRole.Owner, SellerMembershipStatus.Active, now),
                $"SellerMembershipChanged:{seller.Id}:{row.UserId}:Active", now);
        }

        row.Status = approve ? StoreApplicationStatus.Approved : StoreApplicationStatus.Declined;
        row.DecidedAt = now;
        row.ReviewerId = reviewer.UserId;
        row.DeclineReason = reason;
        db.AddAuditEntry(reviewer.UserId, approve ? "Approve store application" : "Decline store application",
            id.ToString("D"), now, sellerId: row.SellerId);
        await db.SaveChangesAsync(ct);
    }

    public async Task<StoreApplicationDocument?> GetVerificationDocumentAsync(ShopActor reviewer, Guid id, StoreDocumentKind kind, CancellationToken ct)
    {
        reviewer.RequireAdmin();
        if (!reviewer.Mfa) throw new ShopRuleException("You cannot perform this action.");
        await using var db = await factory.CreateDbContextAsync(ct);
        var doc = await db.StoreVerificationDocuments.AsNoTracking()
            .FirstOrDefaultAsync(d => d.ApplicationId == id && d.Kind == kind, ct);
        return doc is null ? null : new StoreApplicationDocument(verifierProtector.Unprotect(doc.Data), doc.ContentType);
    }

    public async Task<bool> SetSellerStatusAsync(ShopActor actor, long sellerId, SellerStatus status, string? reason, CancellationToken ct)
    {
        actor.RequireAdmin();
        reason = ShopRules.Text(reason, 250, "Reason", required: status == SellerStatus.Suspended);
        await using var db = await factory.CreateDbContextAsync(ct);
        var seller = await db.Sellers.FirstOrDefaultAsync(s => s.Id == sellerId, ct)
            ?? throw new ShopRuleException("The store was not found.");
        if (seller.Status == status) return false;
        var action = (seller.Status, status) switch
        {
            (SellerStatus.Suspended, SellerStatus.Active) => "Khôi phục gian hàng",
            (SellerStatus.Active, SellerStatus.Suspended) when sellerId != Seller.FirstPartyId => "Tạm ngưng gian hàng",
            _ => throw new ShopRuleException("The store status cannot be changed.")
        };

        var now = time.GetUtcNow();
        seller.Status = status;
        db.AddAuditEntry(actor.UserId, action, sellerId.ToString(CultureInfo.InvariantCulture), now, reason, sellerId);
        db.EnqueueOutboxEvent("Seller", sellerId.ToString(CultureInfo.InvariantCulture), nameof(SellerStatusChanged),
            new SellerStatusChanged(sellerId, seller.DisplayName, status, now),
            $"SellerStatusChanged:{sellerId}:{status}:{now.ToUnixTimeMilliseconds()}", now);
        await db.SaveChangesAsync(ct);
        return true;
    }

    public async Task<bool> ReviewOfferAsync(ShopActor actor, long offerId, bool approve, CancellationToken ct)
    {
        actor.RequireAdmin();
        await using var db = await factory.CreateDbContextAsync(ct);
        var offer = await db.SellerOffers.FirstOrDefaultAsync(o => o.Id == offerId && o.SellerId != Seller.FirstPartyId && o.Status == SellerOfferStatus.Pending, ct)
            ?? throw new ShopRuleException("The offer is no longer pending review.");
        var seller = await db.Sellers.FirstOrDefaultAsync(s => s.Id == offer.SellerId && s.Status == SellerStatus.Active, ct)
            ?? throw new ShopRuleException("The store is no longer active.");
        var sku = await db.Skus.FirstOrDefaultAsync(s => s.Id == offer.SkuId && s.Active, ct);
        var product = sku is null ? null : await db.Products.Include(p => p.Images).Include(p => p.Skus).FirstOrDefaultAsync(p => p.Id == sku.ProductId && p.SellerId == offer.SellerId, ct);

        if (approve)
        {
            if (offer.PriceVnd is null or <= 0 || sku is null || product is null)
                throw new ShopRuleException("The offer requires a price and an active variant owned by the store.");
            var categoryActive = await db.Categories.AnyAsync(c => c.Id == product.CategoryId && c.Active, ct);
            if (product.Status == ProductStatus.Draft) product.Publish(categoryActive);
            else if (product.Status != ProductStatus.Published || !categoryActive)
                throw new ShopRuleException("The product must be published in an active category.");
        }

        var now = time.GetUtcNow();
        offer.Status = approve ? SellerOfferStatus.Approved : SellerOfferStatus.Rejected;
        db.AddAuditEntry(actor.UserId, approve ? "Duyệt chào bán" : "Từ chối chào bán", offer.Id.ToString(CultureInfo.InvariantCulture), now, sellerId: offer.SellerId);
        if (approve && product is not null && sku is not null)
        {
            var bal = await db.InventoryBalances.FirstOrDefaultAsync(b => b.OfferId == offer.Id && b.SellerId == offer.SellerId, ct);
            db.EnqueueOutboxEvent("SellerOffer", offer.Id.ToString(CultureInfo.InvariantCulture), nameof(OfferChanged),
                new OfferChanged(offer.Id, sku.Id, seller.Id, seller.DisplayName, offer.SellerSkuCode, product.Slug,
                    product.Name, offer.PriceVnd ?? 0m, bal?.Available ?? 0, true, now),
                $"OfferChanged:{offer.Id}:{now.ToUnixTimeMilliseconds()}", now);
        }
        await db.SaveChangesAsync(ct);
        return approve;
    }
}
