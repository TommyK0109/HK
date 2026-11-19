using HKShop.Domain;
using HKShop.Domain.Catalog;
using HKShop.Domain.Sellers;

namespace HKShop.Application.Catalog;

public sealed class CatalogCard
{
    public int Id { get; init; }
    public string Slug { get; init; } = "";
    public string Name { get; init; } = "";
    public string Description { get; init; } = "";
    public decimal? Price { get; init; }
    public string SellerName { get; init; } = "";
    public string? FileName { get; init; }
    public string? Alt { get; init; }
    public bool BrowseOnly { get; init; }
}
public enum CatalogBrowseMode { WithOffers, All, Imports }
public sealed record CatalogDetails(Product Product, IReadOnlyList<SkuDisplay> Variants, IReadOnlyList<ProductImage> Images)
{
    public IReadOnlyList<CatalogOfferDisplay> Offers { get; init; } = [];
    public bool BrowseOnly { get; init; }
    public decimal? CatalogPriceVnd { get; init; }
}
public sealed record CatalogOfferDisplay(long Id, long SkuId, string SkuCode, string SellerSkuCode, string SellerName,
    string Size, string Color, decimal Price, int Available);
public sealed record ProductReviewDisplay(long Id, int Rating, string Content, DateTimeOffset CreatedAt, DateTimeOffset? UpdatedAt, bool CanEdit);

public sealed class CatalogService(IShopStore store, IProductImageStore media, TimeProvider time)
{
    public Task<List<Category>> Categories(bool activeOnly = true) => store.Read(db =>
        db.List(db.Query<Category>().Where(c => !activeOnly || c.Active).OrderBy(c => c.Name)));

    public Task<Page<CatalogCard>> Browse(string? search, long? categoryId, string? sort, int page = 1,
        CatalogBrowseMode mode = CatalogBrowseMode.WithOffers) =>
        store.Read(async db =>
        {
            var searchText = (search ?? "").Trim();
            searchText = searchText[..Math.Min(searchText.Length, 100)];
            var offers = StorefrontOffer.Query(db).Where(offer => offer.Active);
            var images = db.Query<ProductImage>();
            var query = from product in db.Query<Product>()
                        where (categoryId == null || product.CategoryId == categoryId)
                            && (searchText == "" || product.Name.Contains(searchText) ||
                                offers.Any(candidate => candidate.ProductId == product.Id &&
                                    (candidate.SkuCode.Contains(searchText) || candidate.SellerSkuCode.Contains(searchText))))
                        from offer in offers.Where(candidate => candidate.ProductId == product.Id)
                            .OrderByDescending(candidate => candidate.Available > 0).ThenBy(candidate => candidate.Price)
                            .ThenBy(candidate => candidate.Id).Take(1)
                        select new CatalogCard
                        {
                            Id = product.Id, Slug = product.Slug, Name = product.Name, Description = product.Description,
                            Price = offer.Price, SellerName = offer.SellerName,
                            FileName = images.Where(i => i.ProductId == product.Id).OrderBy(i => i.Id).Select(i => i.FileName).FirstOrDefault(),
                            Alt = images.Where(i => i.ProductId == product.Id).OrderBy(i => i.Id).Select(i => i.Alt).FirstOrDefault(),
                            BrowseOnly = false
                        };
            if (mode != CatalogBrowseMode.WithOffers)
            {
                var skuRows = db.Query<Sku>();
                var imports = from product in BrowsableImports(db)
                              where (categoryId == null || product.CategoryId == categoryId)
                                  && (searchText == "" || product.Name.Contains(searchText) ||
                                      skuRows.Any(s => s.ProductId == product.Id && s.Code.Contains(searchText)))
                              select new CatalogCard
                              {
                                  Id = product.Id, Slug = product.Slug, Name = product.Name, Description = product.Description,
                                  Price = skuRows.Where(s => s.ProductId == product.Id && s.Active && s.PriceVnd > 0)
                                      .Min(s => s.PriceVnd),
                                  SellerName = "",
                                  FileName = images.Where(i => i.ProductId == product.Id).OrderBy(i => i.Id).Select(i => i.FileName).FirstOrDefault(),
                                  Alt = images.Where(i => i.ProductId == product.Id).OrderBy(i => i.Id).Select(i => i.Alt).FirstOrDefault(),
                                  BrowseOnly = true
                              };
                query = mode == CatalogBrowseMode.Imports ? imports : query.Concat(imports);
            }
            var ordered = sort switch
            {
                "price-asc" => query.OrderBy(p => p.Price == null).ThenBy(p => p.Price).ThenBy(p => p.Id),
                "price-desc" => query.OrderByDescending(p => p.Price).ThenBy(p => p.Id),
                _ => query.OrderByDescending(p => p.Id)
            };
            var count = await db.Count(query);
            var number = Math.Clamp(page, 1, Math.Max(1, (int)Math.Ceiling(count / 24d)));
            return new Page<CatalogCard>(await db.List(ordered.Skip((number - 1) * 24).Take(24)), number, count, 24);
        });

    public Task<CatalogDetails?> Details(string slug, bool includeImports = false) => store.Read<CatalogDetails?>(async db =>
    {
        var product = await FindPublishedProduct(db, slug);
        if (product is null && includeImports && await db.First(BrowsableImports(db).Where(p => p.Slug == slug)) is { } imported)
        {
            var pricedSku = await db.First(db.Query<Sku>().Where(s => s.ProductId == imported.Id && s.Active && s.PriceVnd > 0)
                .OrderBy(s => s.PriceVnd).ThenBy(s => s.Id));
            return new CatalogDetails(imported, [], await ReadImages(db, imported.Id))
            {
                BrowseOnly = true,
                CatalogPriceVnd = pricedSku?.PriceVnd
            };
        }
        if (product is null) return null;
        var offers = await db.List(StorefrontOffer.Query(db).Where(offer => offer.ProductId == product.Id && offer.Active)
            .OrderByDescending(offer => offer.Available > 0).ThenBy(offer => offer.Price)
            .ThenBy(offer => offer.Id)
            .Select(offer => new CatalogOfferDisplay(offer.Id, offer.SkuId, offer.SkuCode, offer.SellerSkuCode,
                offer.SellerName, offer.Size, offer.Color, offer.Price, offer.Available)));
        if (product.SellerId != Seller.FirstPartyId && offers.Count == 0) return null;
        var variants = offers.DistinctBy(offer => offer.SkuId).OrderBy(offer => offer.SkuId).Select(offer =>
            new SkuDisplay(offer.SkuId, offer.SkuCode, offer.Size, offer.Color, offer.Price, true, offer.Available)).ToList();
        return new CatalogDetails(product, variants, await ReadImages(db, product.Id)) { Offers = offers };
    });

    private static IQueryable<Product> BrowsableImports(IShopSession db) =>
        db.Query<Product>().Where(p => p.SellerId == Seller.FirstPartyId && p.Status == ProductStatus.Draft &&
            (p.Slug.StartsWith("abo-") || p.Slug.StartsWith("myntra-")) &&
            db.Query<Category>().Any(c => c.Id == p.CategoryId && c.Active) &&
            db.Query<ProductImage>().Any(image => image.ProductId == p.Id));

    public Task<Page<ProductReviewDisplay>> Reviews(int productId, string? customerId, int page = 1) => store.Read(async db =>
    {
        const int pageSize = 10;
        var query = db.Query<ProductReview>().Where(review => review.ProductId == productId);
        var total = await db.Count(query);
        var number = Math.Clamp(page, 1, Math.Max(1, (int)Math.Ceiling(total / (double)pageSize)));
        var items = await db.List(query.OrderByDescending(review => review.Id).Skip((number - 1) * pageSize).Take(pageSize)
            .Select(review => new ProductReviewDisplay(review.Id, review.Rating, review.Content, review.CreatedAt,
                review.UpdatedAt, customerId != null && review.CustomerId == customerId)));
        return new Page<ProductReviewDisplay>(items, number, total, pageSize);
    });

    public Task<string> SubmitReview(ShopActor actor, string slug, string? content, int rating)
    {
        actor.RequireCustomer();
        return store.Write(async db =>
        {
            var product = await FindPublishedProduct(db, slug)
                ?? throw new ShopRuleException("This product is no longer available for sale.");
            db.Add(ProductReview.Submit(product.Id, actor.UserId, content, rating, time.GetUtcNow()));
            return product.Slug;
        });
    }

    public Task<string> EditReview(ShopActor actor, string slug, long reviewId, string? content, int rating)
    {
        actor.RequireCustomer();
        return store.Write(async db =>
        {
            var product = await FindPublishedProduct(db, slug)
                ?? throw new ShopRuleException("This product is no longer available for sale.");
            var review = await db.First(db.Query<ProductReview>().Where(row =>
                row.Id == reviewId && row.ProductId == product.Id && row.CustomerId == actor.UserId))
                ?? throw new ShopRuleException("Your review was not found.");
            review.Edit(content, rating, time.GetUtcNow());
            return product.Slug;
        });
    }

    public Task<Page<Product>> Products(ShopActor actor, int page)
    {
        actor.RequireStaff();
        return store.Read(async db =>
        {
            var query = db.Query<Product>();
            var total = await db.Count(query);
            var number = Math.Clamp(page, 1, Math.Max(1, (int)Math.Ceiling(total / 25d)));
            return new Page<Product>(await db.List(query.OrderByDescending(p => p.Id).Skip((number - 1) * 25).Take(25)), number, total, 25);
        });
    }

    public Task<CatalogDetails> EditDetails(ShopActor actor, int id)
    {
        actor.RequireStaff();
        return store.Read(async db => await ReadDetails(db, await FindProduct(db, id)));
    }

    public Task<int> SaveCategory(ShopActor actor, long id, string name, bool active)
    {
        actor.RequireStaff();
        name = ShopRules.Text(name, 100, "Category");
        return store.Write(async db =>
        {
            var row = id == 0 ? new Category() : await db.First(db.Query<Category>().Where(c => c.Id == id))
                ?? throw new ShopRuleException("The category does not exist.");
            if (await db.Count(db.Query<Category>().Where(c => c.Name == name && c.Id != id)) > 0)
                throw new ShopRuleException("The category already exists.");
            row.Name = name;
            row.Active = active;
            if (id == 0) db.Add(row);
            await db.Save();
            db.Audit(actor.UserId, "Danh mục", row.Id.ToString(), time.GetUtcNow());
            return 0;
        });
    }

    public Task<int> SaveProduct(ShopActor actor, int id, string name, string slug, string description, long? categoryId)
    {
        actor.RequireStaff();
        name = ShopRules.Text(name, 150, "Product name");
        slug = ShopRules.Text(slug, 150, "Slug").ToLowerInvariant();
        description = ShopRules.Text(description, 4000, "Description", false);
        if (slug.Any(c => !(char.IsAsciiLetterOrDigit(c) || c == '-')))
            throw new ShopRuleException("The slug may contain only Latin letters, digits, and hyphens.");
        return store.Write(async db =>
        {
            var row = id == 0 ? new Product() : await FindProduct(db, id);
            if (await db.Count(db.Query<Product>().Where(p => p.Slug == slug && p.Id != id)) > 0)
                throw new ShopRuleException("The product slug already exists.");
            if (categoryId is not null && await db.First(db.Query<Category>().Where(c => c.Id == categoryId)) is null)
                throw new ShopRuleException("The category does not exist.");
            if (row.Status == ProductStatus.Published && !await HasActiveCategory(db, categoryId))
                throw new ShopRuleException("A published product requires an active category.");
            row.Name = name;
            row.Slug = slug;
            row.Description = description;
            row.CategoryId = categoryId;
            if (id == 0) db.Add(row);
            await db.Save();
            db.Audit(actor.UserId, "Sản phẩm", row.Id.ToString(), time.GetUtcNow());
            return row.Id;
        });
    }

    public Task<long> SaveSku(ShopActor actor, int productId, long skuId, string code, string size, string color, decimal price, bool active)
    {
        actor.RequireStaff();
        code = ShopRules.Text(code, 64, "SKU code").ToUpperInvariant();
        size = ShopRules.Text(size, 40, "Size", false);
        color = ShopRules.Text(color, 40, "Color", false);
        ShopRules.Vnd(price);
        return store.Write(async db =>
        {
            var product = await FindProduct(db, productId);
            var sku = skuId == 0 ? new Sku { ProductId = productId } :
                await db.First(db.Query<Sku>().Where(s => s.Id == skuId && s.ProductId == productId))
                    ?? throw new ShopRuleException("The SKU does not exist.");
            if (await db.Count(db.Query<Sku>().Where(s => s.Code == code && s.Id != skuId)) > 0)
                throw new ShopRuleException("The SKU code already exists.");
            if (product.Status == ProductStatus.Published && !active &&
                await db.Count(db.Query<Sku>().Where(s => s.ProductId == productId && s.Id != skuId && s.Active)) == 0)
                throw new ShopRuleException("Unpublish the product before deactivating its last SKU.");
            sku.Code = code;
            sku.Size = size;
            sku.Color = color;
            sku.PriceVnd = price;
            sku.Active = active;
            if (skuId == 0) db.Add(sku);
            await db.Save();
            db.Audit(actor.UserId, "Biến thể", sku.Id.ToString(), time.GetUtcNow());
            return sku.Id;
        });
    }

    public async Task AddImage(ShopActor actor, int productId, string alt, Stream file)
    {
        actor.RequireStaff();
        alt = ShopRules.Text(alt, 200, "Image description");
        await store.Read(db => FindProduct(db, productId));
        var name = await media.Save(file);
        try
        {
            await store.Write(async db =>
            {
                await FindProduct(db, productId);
                db.Add(new ProductImage { ProductId = productId, FileName = name, Alt = alt });
                db.Audit(actor.UserId, "Ảnh sản phẩm", productId.ToString(), time.GetUtcNow());
                return 0;
            });
        }
        catch
        {
            try { await media.Delete(name); }
            catch { /* Best-effort cleanup of orphaned image after database write failure. */ }
            throw;
        }
    }

    public Task<int> ChangePublication(ShopActor actor, int id, bool publish)
    {
        actor.RequireStaff();
        return store.Write(async db =>
        {
            var product = await FindProduct(db, id);
            if (publish)
            {
                await db.List(db.Query<Sku>().Where(s => s.ProductId == id));
                await db.List(db.Query<ProductImage>().Where(i => i.ProductId == id));
                product.Publish(await HasActiveCategory(db, product.CategoryId));
            }
            else product.Archive();
            db.Audit(actor.UserId, publish ? "Đăng bán" : "Ẩn sản phẩm", id.ToString(), time.GetUtcNow());
            return id;
        });
    }

    internal static async Task<bool> HasActiveCategory(IShopSession db, long? id) =>
        await db.Count(db.Query<Category>().Where(c => c.Id == id && c.Active)) > 0;

    private static async Task<Product> FindProduct(IShopSession db, int id) =>
        await db.First(db.Query<Product>().Where(p => p.Id == id)) ?? throw new ShopRuleException("The product does not exist.");

    private static Task<Product?> FindPublishedProduct(IShopSession db, string slug)
    {
        var categories = db.Query<Category>();
        return db.First(db.Query<Product>().Where(p => p.Slug == slug && p.Status == ProductStatus.Published
            && categories.Any(c => c.Id == p.CategoryId && c.Active)));
    }

    private static async Task<CatalogDetails> ReadDetails(IShopSession db, Product product)
    {
        var offerBalances = InventoryService.ActiveOfferBalances(db);
        var variants = from sku in db.Query<Sku>()
                       join stock in db.Query<StockBalance>() on sku.Id equals stock.SkuId
                       where sku.ProductId == product.Id
                       orderby sku.Id
                       select new SkuDisplay(
                           sku.Id,
                           sku.Code,
                           sku.Size,
                           sku.Color,
                           sku.PriceVnd,
                           sku.Active,
                           stock.OnHand - stock.Reserved,
                           offerBalances.Where(b => b.SkuId == sku.Id).Sum(b => (int?)b.OnHand) ?? 0,
                           offerBalances.Where(b => b.SkuId == sku.Id).Sum(b => (int?)(b.OnHand - b.Reserved)) ?? 0);
        return new CatalogDetails(product, await db.List(variants), await ReadImages(db, product.Id));
    }

    private static Task<List<ProductImage>> ReadImages(IShopSession db, int productId) =>
        db.List(db.Query<ProductImage>().Where(image => image.ProductId == productId).OrderBy(image => image.Id));
}

public sealed class InventoryService(IShopStore store, TimeProvider time)
{
    internal sealed class OfferBalanceRow
    {
        public long SkuId { get; init; }
        public int OnHand { get; init; }
        public int Reserved { get; init; }
    }

    internal static IQueryable<OfferBalanceRow> ActiveOfferBalances(IShopSession db) =>
        from offer in db.Query<SellerOffer>()
        join sku in db.Query<Sku>() on offer.SkuId equals sku.Id
        join product in db.Query<Product>() on sku.ProductId equals product.Id
        join seller in db.Query<Seller>() on offer.SellerId equals seller.Id
        join balance in db.Query<InventoryBalance>()
            on new { OfferId = offer.Id, offer.SellerId } equals new { balance.OfferId, balance.SellerId }
        join location in db.Query<SellerLocation>()
            on new { Id = balance.LocationId, balance.SellerId } equals new { location.Id, location.SellerId }
        where offer.Status == SellerOfferStatus.Approved && seller.Status == SellerStatus.Active && location.Active &&
              product.SellerId == offer.SellerId
        select new OfferBalanceRow { SkuId = offer.SkuId, OnHand = balance.OnHand, Reserved = balance.Reserved };

    public Task<Page<StockRow>> List(ShopActor actor, bool low, int page = 1)
    {
        actor.RequireStaff();
        return store.Read(async db =>
        {
            var offerBalances = ActiveOfferBalances(db);
            var query = from sku in db.Query<Sku>()
                        join product in db.Query<Product>() on sku.ProductId equals product.Id
                        join stock in db.Query<StockBalance>() on sku.Id equals stock.SkuId
                        where product.SellerId == Seller.FirstPartyId &&
                              (!low || (sku.Active && stock.OnHand - stock.Reserved <= stock.LowThreshold))
                        orderby sku.Code
                        select new StockRow(
                            sku.Id,
                            sku.Code,
                            product.Name,
                            stock.OnHand,
                            stock.Reserved,
                            stock.LowThreshold,
                            offerBalances.Where(b => b.SkuId == sku.Id).Sum(b => (int?)b.OnHand) ?? 0,
                            offerBalances.Where(b => b.SkuId == sku.Id).Sum(b => (int?)(b.OnHand - b.Reserved)) ?? 0);
            var count = await db.Count(query);
            var number = Math.Clamp(page, 1, Math.Max(1, (int)Math.Ceiling(count / 25d)));
            return new Page<StockRow>(await db.List(query.Skip((number - 1) * 25).Take(25)), number, count, 25);
        });
    }

    public Task<List<StockMovement>> History(ShopActor actor, long sku)
    {
        actor.RequireStaff();
        return store.Read(db => db.List(db.Query<StockMovement>().Where(m => m.SkuId == sku).OrderByDescending(m => m.Id).Take(100)));
    }

    public Task<int> Adjust(ShopActor actor, long skuId, int delta, int threshold, string reason)
    {
        actor.RequireStaff();
        reason = ShopRules.Text(reason, 250, "Reason");
        if (threshold < 0) throw new ShopRuleException("The stock threshold must be zero or greater.");
        return store.Write(async db =>
        {
            if (await db.First(from sku in db.Query<Sku>()
                               join product in db.Query<Product>() on sku.ProductId equals product.Id
                               where sku.Id == skuId && product.SellerId == Seller.FirstPartyId
                               select sku) is null)
                throw new ShopRuleException("This variant does not belong to HKShop.");
            var stock = await GetBalance(db, skuId);
            stock.Adjust(delta);
            stock.LowThreshold = threshold;
            var now = time.GetUtcNow();
            if (delta != 0) Movement(db, skuId, null, delta, 0, reason, actor.UserId, now);
            var offerBalance = await FindFirstPartyBalance(db, skuId);
            if (offerBalance is not null)
            {
                offerBalance.OnHand = stock.OnHand;
                offerBalance.Reserved = stock.Reserved;
                if (delta != 0) OfferMovement(db, offerBalance.Id, delta, 0, reason, actor.UserId, now);
            }
            db.Audit(actor.UserId, "Cập nhật kho", skuId.ToString(), now, reason);
            return stock.OnHand;
        });
    }

    public static async Task<StockBalance> GetBalance(IShopSession db, long skuId) =>
        await db.First(db.Query<StockBalance>().Where(s => s.SkuId == skuId))
            ?? throw new ShopRuleException("Inventory was not found.");

    public static Task<InventoryBalance?> FindFirstPartyBalance(IShopSession db, long skuId) =>
        db.First(from balance in db.Query<InventoryBalance>()
                 join offer in db.Query<SellerOffer>() on balance.OfferId equals offer.Id
                 join location in db.Query<SellerLocation>() on new { Id = balance.LocationId, balance.SellerId } equals new { location.Id, location.SellerId }
                 where balance.SellerId == Seller.FirstPartyId && offer.SkuId == skuId && location.Active
                 orderby balance.Id
                 select balance);

    public static async Task ReserveOffer(IShopSession db, InventoryBalance balance, long skuId, Guid orderId,
        int quantity, string reason, string? actor, DateTimeOffset now)
    {
        balance.Reserve(quantity);
        OfferMovement(db, balance.Id, 0, quantity, reason, actor, now);
        if (balance.SellerId == Seller.FirstPartyId)
        {
            var legacy = await db.First(db.Query<StockBalance>().Where(s => s.SkuId == skuId));
            if (legacy is not null)
            {
                legacy.Reserve(quantity);
                Movement(db, skuId, orderId, 0, quantity, reason, actor, now);
            }
        }
    }

    public static async Task ReleaseOffer(IShopSession db, InventoryBalance balance, long skuId, Guid orderId,
        int quantity, string reason, string? actor, DateTimeOffset now)
    {
        balance.Release(quantity);
        OfferMovement(db, balance.Id, 0, -quantity, reason, actor, now);
        if (balance.SellerId == Seller.FirstPartyId)
        {
            var legacy = await db.First(db.Query<StockBalance>().Where(s => s.SkuId == skuId));
            if (legacy is not null)
            {
                legacy.Release(quantity);
                Movement(db, skuId, orderId, 0, -quantity, reason, actor, now);
            }
        }
    }

    public static async Task DispatchOffer(IShopSession db, InventoryBalance balance, long skuId, Guid orderId,
        int quantity, string reason, string? actor, DateTimeOffset now)
    {
        balance.Dispatch(quantity);
        OfferMovement(db, balance.Id, -quantity, -quantity, reason, actor, now);
        if (balance.SellerId == Seller.FirstPartyId)
        {
            var legacy = await db.First(db.Query<StockBalance>().Where(s => s.SkuId == skuId));
            if (legacy is not null)
            {
                legacy.Dispatch(quantity);
                Movement(db, skuId, orderId, -quantity, -quantity, reason, actor, now);
            }
        }
    }

    public static async Task RestockOffer(IShopSession db, InventoryBalance balance, long skuId, Guid orderId,
        int quantity, string reason, string? actor, DateTimeOffset now)
    {
        balance.Restock(quantity);
        OfferMovement(db, balance.Id, quantity, 0, reason, actor, now);
        if (balance.SellerId == Seller.FirstPartyId)
        {
            var legacy = await db.First(db.Query<StockBalance>().Where(s => s.SkuId == skuId));
            if (legacy is not null)
            {
                legacy.Adjust(quantity);
                Movement(db, skuId, orderId, quantity, 0, reason, actor, now);
            }
        }
    }

    public static void OfferMovement(IShopSession db, long inventoryBalanceId, int onHand, int reserved,
        string reason, string? actor, DateTimeOffset now) =>
        db.Add(new OfferStockMovement
        {
            InventoryBalanceId = inventoryBalanceId, OnHandDelta = onHand, ReservedDelta = reserved,
            Reason = reason, ActorId = actor, OccurredAt = now
        });

    public static void Movement(IShopSession db, long skuId, Guid? orderId, int onHand, int reserved, string reason, string? actor, DateTimeOffset now) =>
        db.Add(new StockMovement
        {
            SkuId = skuId, OrderId = orderId, OnHandDelta = onHand, ReservedDelta = reserved,
            Reason = reason, ActorId = actor, OccurredAt = now
        });
}
