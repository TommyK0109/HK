using System.Globalization;
using HKShop.Application.Catalog;
using HKShop.Domain;
using HKShop.Domain.Catalog;
using HKShop.Domain.Sellers;

namespace HKShop.Application.Sellers;

public sealed record SellerCatalogVariant(long Id, string Product, string Code, string Size, string Color);
public sealed record SellerProductVariant(string Code, string? Size, string? Color, decimal Price, int Quantity);
public sealed record SellerOfferRow(long Id, string Code, string Product, string Variant, decimal? PriceVnd,
    SellerOfferStatus Status, int OnHand, int Reserved)
{
    public int Available => OnHand - Reserved;
}
public sealed record SellerCatalogView(SellerWorkspace Seller, Page<SellerOfferRow> Offers, Page<ProductProposal> Proposals,
    IReadOnlyList<Category> Categories, IReadOnlyList<SellerCatalogVariant> Variants, SellerOfferRow? HistoryOffer,
    IReadOnlyList<OfferStockMovement> History);

public sealed class SellerCatalogService(IShopStore store, TimeProvider time)
{
    public Task<SellerCatalogView> Catalog(ShopActor actor, long sellerId, string? search, int offerPage, int proposalPage,
        long? historyOfferId) => store.Read(async db =>
    {
        var seller = await RequireManagedSeller(db, actor, sellerId);
        search = (search ?? "").Trim();
        search = search[..Math.Min(search.Length, 100)];
        var offers = await ReadPage(db, OfferRows(db, sellerId), offerPage);
        var proposals = await ReadPage(db, db.Query<ProductProposal>().Where(p => p.SellerId == sellerId)
            .OrderByDescending(p => p.Id), proposalPage);
        var categories = await db.List(db.Query<Category>().Where(c => c.Active).OrderBy(c => c.Name));
        var listed = db.Query<SellerOffer>().Where(o => o.SellerId == sellerId);
        var variants = await db.List((
            from sku in ApprovedVariants(db, sellerId)
            join product in db.Query<Product>() on sku.ProductId equals product.Id
            where !listed.Any(o => o.SkuId == sku.Id) &&
                (search == "" || sku.Code.Contains(search) || product.Name.Contains(search))
            orderby product.Id descending, sku.Id
            select new SellerCatalogVariant(sku.Id, product.Name, sku.Code, sku.Size, sku.Color)).Take(25));

        SellerOfferRow? historyOffer = null;
        List<OfferStockMovement> history = [];
        if (historyOfferId is long offerId)
        {
            historyOffer = await db.First(OfferRows(db, sellerId, offerId)) ?? throw new SellerAccessDeniedException();
            var balances = db.Query<InventoryBalance>().Where(b => b.SellerId == sellerId && b.OfferId == offerId);
            history = await db.List(db.Query<OfferStockMovement>()
                .Where(m => balances.Any(b => b.Id == m.InventoryBalanceId)).OrderByDescending(m => m.Id).Take(100));
        }

        return new SellerCatalogView(seller, offers, proposals, categories, variants, historyOffer, history);
    });

    public Task<long> CreateOffer(ShopActor actor, long sellerId, long skuId, string code, decimal price, int initialQuantity = 0) =>
        store.Write(async db =>
        {
            await RequireManagedSeller(db, actor, sellerId);
            code = NormalizeCode(code);
            ValidatePrice(price);
            if (initialQuantity < 0)
                throw new ShopRuleException("Initial stock quantity cannot be negative.");
            if (await db.First(ApprovedVariants(db, sellerId).Where(s => s.Id == skuId)) is null)
                throw new ShopRuleException("This variant is not available for listing.");
            if (await db.First(db.Query<SellerOffer>().Where(o => o.SellerId == sellerId && o.SkuId == skuId)) is not null)
                throw new ShopRuleException("This store already has an offer for this variant.");
            await RequireUniqueCode(db, sellerId, code, 0);

            var location = await OfferLocation(db, sellerId);
            return await AddPendingOffer(db, location, skuId, code, price, initialQuantity, actor.UserId, time.GetUtcNow());
        });

    public async Task<int> SubmitProduct(ShopActor actor, long sellerId, string name, string? description, long categoryId,
        IReadOnlyList<SellerProductVariant> variants, IReadOnlyList<Func<Stream>> photos, IProductImageStore media)
    {
        name = ShopRules.Text(name, 150, "Product name");
        description = ShopRules.Text(description, 4000, "Description", required: false);
        if (variants.Count is < 1 or > 50 || photos.Count is < 1 or > 4)
            throw new ShopRuleException("Add 1 to 50 variants and 1 to 4 product photos.");

        var rows = variants.Select(row =>
        {
            ValidatePrice(row.Price);
            if (row.Quantity < 0) throw new ShopRuleException("Initial stock cannot be negative.");
            return row with
            {
                Code = NormalizeCode(row.Code),
                Size = ShopRules.Text(row.Size, 40, "Size", required: false),
                Color = ShopRules.Text(row.Color, 40, "Color", required: false)
            };
        }).ToList();
        if (rows.Select(row => row.Code).Distinct(StringComparer.OrdinalIgnoreCase).Count() != rows.Count)
            throw new ShopRuleException("Each variant needs a different offer code.");

        await store.Read(db => RequireManagedSeller(db, actor, sellerId));
        var imageNames = new List<string>();
        try
        {
            foreach (var openPhoto in photos)
            {
                await using var photo = openPhoto();
                imageNames.Add(await media.Save(photo));
            }
            return await store.Write(async db =>
            {
                await RequireManagedSeller(db, actor, sellerId);
                if (!await CatalogService.HasActiveCategory(db, categoryId))
                    throw new ShopRuleException("The category is no longer active.");
                var codes = rows.Select(row => row.Code).ToArray();
                if (await db.First(db.Query<SellerOffer>().Where(offer =>
                    offer.SellerId == sellerId && codes.Contains(offer.SellerSkuCode))) is not null)
                    throw new ShopRuleException("An offer code is already in use by the store.");

                var product = new Product
                {
                    SellerId = sellerId, Name = name, Description = description, CategoryId = categoryId,
                    Slug = $"product-{Guid.NewGuid():N}",
                    Images = imageNames.Select(file => new ProductImage { FileName = file, Alt = name }).ToList(),
                    Skus = rows.Select(row => new Sku
                    {
                        Code = $"SKU-{Guid.NewGuid():N}", Size = row.Size ?? "", Color = row.Color ?? "", Active = true
                    }).ToList()
                };
                db.Add(product);
                var location = await OfferLocation(db, sellerId);
                await db.Save();
                var now = time.GetUtcNow();
                for (var index = 0; index < rows.Count; index++)
                {
                    var row = rows[index];
                    await AddPendingOffer(db, location, product.Skus[index].Id, row.Code, row.Price, row.Quantity, actor.UserId, now);
                }
                return product.Id;
            });
        }
        catch
        {
            foreach (var file in imageNames)
            {
                try { await media.Delete(file); }
                catch { /* Best-effort cleanup after failed submission. */ }
            }
            throw;
        }
    }

    public Task<bool> UpdateOffer(ShopActor actor, long sellerId, long offerId, string code, decimal price) =>
        store.Write(async db =>
        {
            await RequireManagedSeller(db, actor, sellerId);
            var offer = await FindOffer(db, sellerId, offerId);
            code = NormalizeCode(code);
            ValidatePrice(price);
            await RequireUniqueCode(db, sellerId, code, offerId);
            var resubmit = offer.Status is SellerOfferStatus.Rejected or SellerOfferStatus.Archived;
            if (resubmit)
            {
                if (await db.First(OwnedVariants(db, sellerId).Where(s => s.Id == offer.SkuId && s.Active)) is null)
                    throw new ShopRuleException("This variant is not available for listing.");
                offer.Status = SellerOfferStatus.Pending;
            }
            offer.SellerSkuCode = code;
            offer.PriceVnd = price;
            db.Audit(actor.UserId, resubmit ? "Gửi lại chào bán" : "Cập nhật chào bán", Key(offerId),
                time.GetUtcNow(), sellerId: sellerId);
            return resubmit;
        });

    public Task<bool> ArchiveOffer(ShopActor actor, long sellerId, long offerId) => store.Write(async db =>
    {
        await RequireManagedSeller(db, actor, sellerId);
        var offer = await FindOffer(db, sellerId, offerId);
        if (offer.Status == SellerOfferStatus.Archived) return false;
        offer.Status = SellerOfferStatus.Archived;
        db.Audit(actor.UserId, "Ngừng chào bán", Key(offerId), time.GetUtcNow(), sellerId: sellerId);
        return true;
    });

    public Task<int> AdjustStock(ShopActor actor, long sellerId, long offerId, int delta, string reason) => store.Write(async db =>
    {
        await RequireManagedSeller(db, actor, sellerId);
        var offer = await FindOffer(db, sellerId, offerId);
        if (offer.Status == SellerOfferStatus.Archived) throw new ShopRuleException("The offer is no longer active.");
        var balance = await db.First(from stock in db.Query<InventoryBalance>()
                                     join location in db.Query<SellerLocation>() on stock.LocationId equals location.Id
                                     where stock.SellerId == sellerId && stock.OfferId == offerId && location.Active
                                     select stock) ?? throw new ShopRuleException("Inventory was not found.");
        reason = ShopRules.Text(reason, 250, "Reason");
        if (delta == 0) throw new ShopRuleException("The stock adjustment must not be zero.");
        var quantity = (long)balance.OnHand + delta;
        if (quantity < balance.Reserved || quantity > int.MaxValue)
            throw new ShopRuleException("Stock cannot be lower than the reserved quantity or exceed the limit.");

        balance.OnHand = (int)quantity;
        var now = time.GetUtcNow();
        db.Add(new OfferStockMovement
        {
            InventoryBalanceId = balance.Id, OnHandDelta = delta, Reason = reason, ActorId = actor.UserId, OccurredAt = now
        });
        if (sellerId == Seller.FirstPartyId)
        {
            var legacy = await db.First(db.Query<StockBalance>().Where(s => s.SkuId == offer.SkuId));
            if (legacy is not null)
            {
                legacy.Adjust(delta);
                InventoryService.Movement(db, offer.SkuId, null, delta, 0, reason, actor.UserId, now);
            }
        }
        db.Audit(actor.UserId, "Cập nhật kho chào bán", Key(offerId), now, reason, sellerId);
        return balance.OnHand;
    });

    public Task<long> ProposeProduct(ShopActor actor, long sellerId, string name, string slug, string? description, long categoryId) =>
        store.Write(async db =>
        {
            await RequireManagedSeller(db, actor, sellerId);
            name = ShopRules.Text(name, 150, "Product name");
            slug = ShopRules.Text(slug, 150, "Slug").ToLowerInvariant();
            if (slug.Any(c => !(char.IsAsciiLetterOrDigit(c) || c == '-')))
                throw new ShopRuleException("The slug may contain only Latin letters, digits, and hyphens.");
            description = ShopRules.Text(description, 4000, "Description", false);
            await RequireNewProduct(db, slug, categoryId);
            if (await db.First(db.Query<ProductProposal>().Where(p => p.SellerId == sellerId && p.Slug == slug &&
                p.Status == ProductProposalStatus.Pending)) is not null)
                throw new ShopRuleException("This store has already submitted this proposal.");
            var now = time.GetUtcNow();
            var proposal = new ProductProposal
            {
                SellerId = sellerId, Name = name, Slug = slug, Description = description, CategoryId = categoryId,
                Status = ProductProposalStatus.Pending, CreatedAt = now
            };
            db.Add(proposal);
            await db.Save();
            db.Audit(actor.UserId, "Đề xuất sản phẩm", Key(proposal.Id), now, sellerId: sellerId);
            return proposal.Id;
        });

    public Task<MarketplaceModeration> Moderation(ShopActor actor, int offerPage, int proposalPage)
    {
        actor.RequireAdmin();
        return store.Read(async db =>
        {
            var offers = from offer in db.Query<SellerOffer>()
                         join seller in db.Query<Seller>() on offer.SellerId equals seller.Id
                         join sku in db.Query<Sku>() on offer.SkuId equals sku.Id
                         join product in db.Query<Product>() on sku.ProductId equals product.Id
                         where offer.SellerId != Seller.FirstPartyId && offer.Status == SellerOfferStatus.Pending &&
                               product.SellerId == offer.SellerId && seller.Status == SellerStatus.Active
                         orderby offer.Id
                         select new OfferReviewRow(offer.Id, seller.DisplayName, product.Name, sku.Code,
                             offer.SellerSkuCode, offer.PriceVnd, product.Id);
            var proposals = from proposal in db.Query<ProductProposal>()
                            join seller in db.Query<Seller>() on proposal.SellerId equals seller.Id
                            join category in db.Query<Category>() on proposal.CategoryId equals category.Id
                            where proposal.Status == ProductProposalStatus.Pending
                            orderby proposal.Id
                            select new ProductProposalReview(proposal.Id, seller.DisplayName, proposal.Name, proposal.Slug,
                                proposal.Description, category.Name);
            return new MarketplaceModeration(await ReadPage(db, offers, offerPage), await ReadPage(db, proposals, proposalPage));
        });
    }

    public Task<bool> ReviewOffer(ShopActor actor, long offerId, bool approve)
    {
        actor.RequireAdmin();
        return store.Write(async db =>
        {
            var offer = await db.First(db.Query<SellerOffer>().Where(o =>
                o.Id == offerId && o.SellerId != Seller.FirstPartyId && o.Status == SellerOfferStatus.Pending))
                ?? throw new ShopRuleException("The offer is no longer pending review.");
            if (approve)
            {
                await RequireActiveSeller(db, offer.SellerId);
                var sku = await db.First(OwnedVariants(db, offer.SellerId).Where(s => s.Id == offer.SkuId && s.Active));
                if (offer.PriceVnd is null or <= 0 || sku is null)
                    throw new ShopRuleException("The offer requires a price and an active variant owned by the store.");
                var product = (await db.First(db.Query<Product>().Where(p => p.Id == sku.ProductId)))!;
                var categoryActive = await CatalogService.HasActiveCategory(db, product.CategoryId);
                if (product.Status == ProductStatus.Draft)
                {
                    await db.List(db.Query<ProductImage>().Where(i => i.ProductId == product.Id));
                    product.Publish(categoryActive);
                }
                else if (product.Status != ProductStatus.Published || !categoryActive)
                    throw new ShopRuleException("The product must be published in an active category.");
            }
            offer.Status = approve ? SellerOfferStatus.Approved : SellerOfferStatus.Rejected;
            db.Audit(actor.UserId, approve ? "Duyệt chào bán" : "Từ chối chào bán", Key(offer.Id),
                time.GetUtcNow(), sellerId: offer.SellerId);
            return approve;
        });
    }

    public Task<int?> ReviewProposal(ShopActor actor, long proposalId, bool approve)
    {
        actor.RequireAdmin();
        return store.Write<int?>(async db =>
        {
            var proposal = await db.First(db.Query<ProductProposal>().Where(p =>
                p.Id == proposalId && p.Status == ProductProposalStatus.Pending))
                ?? throw new ShopRuleException("The proposal is no longer pending review.");
            if (approve)
            {
                await RequireActiveSeller(db, proposal.SellerId);
                await RequireNewProduct(db, proposal.Slug, proposal.CategoryId);
                var product = new Product
                {
                    SellerId = proposal.SellerId, Name = proposal.Name, Slug = proposal.Slug,
                    Description = proposal.Description, CategoryId = proposal.CategoryId
                };
                db.Add(product);
                await db.Save();
                proposal.ProductId = product.Id;
            }
            proposal.Status = approve ? ProductProposalStatus.Approved : ProductProposalStatus.Rejected;
            db.Audit(actor.UserId, approve ? "Duyệt đề xuất sản phẩm" : "Từ chối đề xuất sản phẩm", Key(proposal.Id),
                time.GetUtcNow(), sellerId: proposal.SellerId);
            return proposal.ProductId;
        });
    }

    private static IQueryable<Sku> ApprovedVariants(IShopSession db, long sellerId) =>
        from sku in OwnedVariants(db, sellerId)
        join product in db.Query<Product>() on sku.ProductId equals product.Id
        join category in db.Query<Category>() on product.CategoryId equals (long?)category.Id
        where sku.Active && product.Status == ProductStatus.Published && category.Active
        select sku;

    private static IQueryable<Sku> OwnedVariants(IShopSession db, long sellerId) =>
        from sku in db.Query<Sku>()
        join product in db.Query<Product>() on sku.ProductId equals product.Id
        where product.SellerId == sellerId
        select sku;

    private static IQueryable<SellerOfferRow> OfferRows(IShopSession db, long sellerId, long? offerId = null) =>
        from offer in db.Query<SellerOffer>()
        join sku in db.Query<Sku>() on offer.SkuId equals sku.Id
        join product in db.Query<Product>() on sku.ProductId equals product.Id
        join balance in db.Query<InventoryBalance>() on new { offer.SellerId, OfferId = offer.Id }
            equals new { balance.SellerId, balance.OfferId }
        join location in db.Query<SellerLocation>() on balance.LocationId equals location.Id
        where offer.SellerId == sellerId && product.SellerId == sellerId && location.Active && (offerId == null || offer.Id == offerId)
        orderby offer.Id descending
        select new SellerOfferRow(offer.Id, offer.SellerSkuCode, product.Name, sku.Code, offer.PriceVnd,
            offer.Status, balance.OnHand, balance.Reserved);

    private static async Task<SellerOffer> FindOffer(IShopSession db, long sellerId, long offerId) =>
        await db.First(from offer in db.Query<SellerOffer>()
                       join sku in OwnedVariants(db, sellerId) on offer.SkuId equals sku.Id
                       where offer.SellerId == sellerId && offer.Id == offerId
                       select offer)
            ?? throw new SellerAccessDeniedException();

    private static async Task<SellerWorkspace> RequireManagedSeller(IShopSession db, ShopActor actor, long sellerId)
    {
        if (sellerId == Seller.FirstPartyId) throw new SellerAccessDeniedException();
        return await SellerAccess.Require(db, actor, sellerId, SellerPermission.Catalog);
    }

    private static async Task RequireUniqueCode(IShopSession db, long sellerId, string code, long offerId)
    {
        if (await db.First(db.Query<SellerOffer>().Where(o => o.SellerId == sellerId && o.SellerSkuCode == code && o.Id != offerId))
            is not null) throw new ShopRuleException("This offer code is already in use by the store.");
    }

    private static async Task<SellerLocation> OfferLocation(IShopSession db, long sellerId)
    {
        var location = await db.First(db.Query<SellerLocation>().Where(l => l.SellerId == sellerId && l.Active));
        if (location is not null) return location;
        location = new SellerLocation { SellerId = sellerId, Name = "Main warehouse", Active = true };
        db.Add(location);
        return location;
    }

    private static async Task<long> AddPendingOffer(IShopSession db, SellerLocation location, long skuId, string code,
        decimal price, int quantity, string actorId, DateTimeOffset now)
    {
        var offer = new SellerOffer
        {
            SellerId = location.SellerId, SkuId = skuId, SellerSkuCode = code, PriceVnd = price, Status = SellerOfferStatus.Pending
        };
        db.Add(offer);
        await db.Save();
        var balance = new InventoryBalance { SellerId = location.SellerId, OfferId = offer.Id, LocationId = location.Id, OnHand = quantity };
        db.Add(balance);
        if (quantity > 0)
        {
            await db.Save();
            db.Add(new OfferStockMovement
            {
                InventoryBalanceId = balance.Id, OnHandDelta = quantity, Reason = "Initial offer stock",
                ActorId = actorId, OccurredAt = now
            });
        }
        db.Audit(actorId, "Gửi duyệt chào bán", Key(offer.Id), now, sellerId: location.SellerId);
        return offer.Id;
    }

    private static async Task RequireNewProduct(IShopSession db, string slug, long categoryId)
    {
        if (!await CatalogService.HasActiveCategory(db, categoryId)) throw new ShopRuleException("The category is no longer active.");
        if (await db.First(db.Query<Product>().Where(p => p.Slug == slug)) is not null)
            throw new ShopRuleException("The product slug already exists. Choose a different slug.");
    }

    private static async Task RequireActiveSeller(IShopSession db, long sellerId)
    {
        if (await db.First(db.Query<Seller>().Where(s => s.Id == sellerId && s.Status == SellerStatus.Active)) is null)
            throw new ShopRuleException("The store is no longer active.");
    }

    private static async Task<Page<T>> ReadPage<T>(IShopSession db, IQueryable<T> query, int page)
    {
        const int size = 25;
        var count = await db.Count(query);
        var number = Math.Clamp(page, 1, Math.Max(1, (int)Math.Ceiling(count / (double)size)));
        return new Page<T>(await db.List(query.Skip((number - 1) * size).Take(size)), number, count, size);
    }

    private static void ValidatePrice(decimal price)
    {
        ShopRules.Vnd(price);
        if (price == 0) throw new ShopRuleException("The selling price must be greater than zero.");
    }

    private static string NormalizeCode(string code) => ShopRules.Text(code, 64, "Offer code").ToUpperInvariant();
    private static string Key(long id) => id.ToString(CultureInfo.InvariantCulture);
}
