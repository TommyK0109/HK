using System.Security.Cryptography;
using System.Text.Json;
using HKShop.Application.Catalog;
using HKShop.Application.Settlement;
using HKShop.Application.Transportation;
using HKShop.Contracts.Ports;
using HKShop.Domain;
using HKShop.Domain.Cart;
using HKShop.Domain.Catalog;
using HKShop.Domain.Orders;
using HKShop.Domain.Promotions;
using HKShop.Domain.Sellers;

namespace HKShop.Application.Checkout;

public sealed record CartLine(long OfferId, long SkuId, long SellerId, string SellerName, string Code, string Slug,
    string Name, int Quantity, int Available, decimal Price, bool Active, bool PriceChanged);
public sealed record CartView(Guid Revision, IReadOnlyList<CartLine> Lines)
{
    public decimal Estimate => Lines.Where(line => line.Active).Sum(line => line.Price * line.Quantity);
    public bool SupportsCod => Lines.Count > 0 && Lines.All(line => line.SellerId == Seller.FirstPartyId);
}

public sealed record CheckoutLineQuote(
    long OfferId,
    long SkuId,
    long SellerId,
    string SellerName,
    string Code,
    string Slug,
    string Name,
    int Quantity,
    int Available,
    decimal Price,
    decimal LineSubtotal,
    decimal SellerDiscountVnd,
    decimal PlatformDiscountVnd,
    int CommissionBps,
    decimal CommissionVnd);

public sealed record SellerOrderQuote(
    long SellerId,
    string SellerName,
    decimal SubtotalVnd,
    decimal SellerDiscountVnd,
    decimal PlatformDiscountVnd,
    decimal ShippingVnd,
    decimal CommissionVnd,
    IReadOnlyList<CheckoutLineQuote> Lines)
{
    public decimal DiscountVnd => SellerDiscountVnd + PlatformDiscountVnd;
    public decimal TotalVnd => SubtotalVnd - SellerDiscountVnd - PlatformDiscountVnd + ShippingVnd;
}

public sealed record CheckoutPreview(
    CheckoutQuote Quote,
    CustomerAddress Address,
    IReadOnlyList<CartLine> Lines,
    IReadOnlyList<SellerOrderQuote> Sellers,
    decimal Subtotal,
    decimal Discount,
    decimal Shipping)
{
    public decimal Total => Subtotal - Discount + Shipping;
    public bool SupportsCod => Sellers.Count == 1 && Sellers[0].SellerId == Seller.FirstPartyId;
}

public sealed class AddressService(IShopStore store)
{
    public Task<List<CustomerAddress>> List(string customerId) => store.Read(db =>
        db.List(db.Query<CustomerAddress>().Where(a => a.CustomerId == customerId).OrderBy(a => a.Id)));

    public Task<int> Save(string customerId, long id, string recipient, string phone, string address,
        double? latitude = null, double? longitude = null)
    {
        recipient = ShopRules.Text(recipient, 100, "Recipient");
        phone = ShopRules.Text(phone, 20, "Phone number");
        if (phone.Count(char.IsAsciiDigit) is < 9 or > 15 ||
            phone.Any(c => !(char.IsAsciiDigit(c) || c is '+' or ' ' or '-')))
            throw new ShopRuleException("The phone number is invalid.");
        address = ShopRules.Text(address, 300, "Address");
        if (latitude.HasValue || longitude.HasValue)
            TransportPoint.Require(latitude, longitude, "the delivery address");
        return store.Write(async db =>
        {
            var row = id == 0 ? new CustomerAddress { CustomerId = customerId } : await Find(db, customerId, id);
            row.Recipient = recipient;
            row.Phone = phone;
            row.AddressText = address;
            row.Latitude = latitude;
            row.Longitude = longitude;
            if (id == 0) db.Add(row);
            return 0;
        });
    }

    public Task<int> Delete(string customerId, long id) => store.Write(async db =>
    {
        db.Remove(await Find(db, customerId, id));
        return 0;
    });

    internal static async Task<CustomerAddress> Find(IShopSession db, string customerId, long id) =>
        await db.First(db.Query<CustomerAddress>().Where(a => a.Id == id && a.CustomerId == customerId))
            ?? throw new ShopRuleException("The shipping address does not exist.");
}

public sealed class CartService(IShopStore store, TimeProvider time)
{
    public Task<CartView> Get(string? customerId, string? guestHash) => store.Read(async db =>
    {
        var cart = await Find(db, customerId, guestHash, time.GetUtcNow());
        return cart is null ? new CartView(Guid.Empty, []) : new CartView(cart.Revision, await ReadLines(db, cart.Id));
    });

    public Task<int> Set(string? customerId, string? guestHash, long skuId, int quantity, bool add, ShopActor? actor = null) =>
        SetLine(customerId, guestHash, skuId, quantity, add, bySku: true, actor);

    public Task<int> SetOffer(string? customerId, string? guestHash, long offerId, int quantity, bool add, ShopActor? actor = null) =>
        SetLine(customerId, guestHash, offerId, quantity, add, bySku: false, actor);

    private Task<int> SetLine(string? customerId, string? guestHash, long id, int quantity, bool add, bool bySku, ShopActor? actor)
    {
        if (actor?.IsEmployee == true)
            throw new ShopRuleException("Employee and shipper accounts cannot use shopping features.");
        return store.Write(async db =>
        {
            if (quantity < 0 || quantity > 999 || (add && quantity == 0))
                throw new ShopRuleException("Quantity must be between 1 and 999, or choose to remove the item.");
            var now = time.GetUtcNow();
            var cart = await GetOrCreate(db, customerId, guestHash, now);
            var offerId = bySku ? (await db.First(db.Query<SellerOffer>().Where(offer =>
                offer.SellerId == Seller.FirstPartyId && offer.SkuId == id)))?.Id ?? 0 : id;
            var existing = cart.Items.SingleOrDefault(item => item.OfferId == offerId);
            var next = add ? (existing?.Quantity ?? 0) + quantity : quantity;
            if (next == 0)
            {
                if (existing is not null) cart.Items.Remove(existing);
            }
            else
            {
                var offer = await db.First(StorefrontOffer.Query(db).Where(offer => offer.Id == offerId && offer.Active));
                if (offer is null || next > Math.Min(offer.Available, 999))
                    throw new ShopRuleException("The requested quantity exceeds the available stock.");
                if (existing is null)
                {
                    existing = new CartItem { OfferId = offer.Id, SkuId = offer.SkuId };
                    cart.Items.Add(existing);
                }
                existing.Quantity = next;
                existing.SeenUnitPriceVnd = offer.Price;
            }
            Touch(cart, now);
            return next;
        });
    }

    public Task<int> Merge(string customerId, string guestHash) => store.Write(async db =>
    {
        var now = time.GetUtcNow();
        var guest = await Find(db, null, guestHash, now);
        if (guest is null) return 0;
        var customer = await GetOrCreate(db, customerId, null, now);
        var adjustments = await AddAvailable(db, customer, guest.Items.Select(item => (item.OfferId, item.Quantity)));
        db.Remove(guest);
        Touch(customer, now);
        return adjustments;
    });

    public Task<int> BuyAgain(ShopActor actor, Guid orderId)
    {
        actor.RequireCustomer();
        return store.Write(async db =>
        {
            var order = await db.First(db.Query<Order>().Where(o => o.Id == orderId && o.CustomerId == actor.UserId))
                ?? throw new ShopRuleException("Order not found.");
            order.RequireStatus(OrderStatus.PaymentFailed, OrderStatus.Expired, OrderStatus.Cancelled);
            var now = time.GetUtcNow();
            var cart = await GetOrCreate(db, actor.UserId, null, now);
            var items = await db.List(db.Query<OrderItem>()
                .Where(item => item.OrderId == orderId)
                .OrderBy(item => item.Id)
                .Select(item => new { item.OfferId, item.Quantity }));
            var adjustments = await AddAvailable(db, cart, items.Select(item => (item.OfferId, item.Quantity)));
            Touch(cart, now);
            return adjustments;
        });
    }

    private static async Task<int> AddAvailable(IShopSession db, ShoppingCart cart, IEnumerable<(long OfferId, int Quantity)> items)
    {
        var adjustments = 0;
        foreach (var item in items)
        {
            var existing = cart.Items.SingleOrDefault(line => line.OfferId == item.OfferId);
            var requested = (existing?.Quantity ?? 0) + item.Quantity;
            var offer = await db.First(StorefrontOffer.Query(db).Where(offer => offer.Id == item.OfferId && offer.Active));
            var quantity = Math.Min(Math.Min(requested, 999), offer?.Available ?? 0);
            if (quantity < requested) adjustments++;
            if (offer is null || quantity == 0)
            {
                if (existing is not null) cart.Items.Remove(existing);
                continue;
            }
            if (existing is null)
            {
                existing = new CartItem { OfferId = offer.Id, SkuId = offer.SkuId, SeenUnitPriceVnd = offer.Price };
                cart.Items.Add(existing);
            }
            existing.Quantity = quantity;
        }
        return adjustments;
    }

    private static async Task<ShoppingCart> GetOrCreate(IShopSession db, string? user, string? guest, DateTimeOffset now)
    {
        if (user is null && guest is null) throw new ShopRuleException("Please enable cookies to use the shopping cart.");
        var cart = await Find(db, user, guest, now);
        if (cart is not null) return cart;
        if (user is null)
        {
            var old = await db.First(db.Query<ShoppingCart>().Where(c => c.GuestTokenHash == guest));
            if (old is not null)
            {
                db.Remove(old);
                await db.Save();
            }
        }
        cart = new ShoppingCart { CustomerId = user, GuestTokenHash = user is null ? guest : null, UpdatedAt = now };
        db.Add(cart);
        return cart;
    }

    internal static async Task<ShoppingCart?> Find(IShopSession db, string? user, string? guest, DateTimeOffset now)
    {
        if (user is null && guest is null) return null;
        var cutoff = now.AddDays(-30);
        var cart = await db.First(db.Query<ShoppingCart>().Where(c => user != null ? c.CustomerId == user :
            c.GuestTokenHash == guest && c.UpdatedAt >= cutoff));
        if (cart is not null)
            cart.Items = await db.List(db.Query<CartItem>().Where(i => i.CartId == cart.Id));
        return cart;
    }

    internal static void Touch(ShoppingCart cart, DateTimeOffset now)
    {
        cart.Revision = Guid.NewGuid();
        cart.UpdatedAt = now;
    }

    internal static Task<List<CartLine>> ReadLines(IShopSession db, long cartId) =>
        db.List(from line in db.Query<CartItem>()
                join offer in StorefrontOffer.Query(db) on line.OfferId equals offer.Id
                where line.CartId == cartId
                orderby offer.SellerId, line.SkuId, line.OfferId
                select new CartLine(line.OfferId, line.SkuId, offer.SellerId, offer.SellerName, offer.SellerSkuCode,
                    offer.Slug, offer.Name, line.Quantity, offer.Available, offer.Price, offer.Active,
                    line.SeenUnitPriceVnd != null && line.SeenUnitPriceVnd != offer.Price));
}

public sealed class PromotionService(IShopStore store, TimeProvider time)
{
    public Task<List<Promotion>> List(ShopActor actor)
    {
        actor.RequireAdmin();
        return store.Read(db => db.List(db.Query<Promotion>().OrderByDescending(p => p.Id)));
    }

    public Task<int> Save(ShopActor actor, long id, string code, bool active, bool percentage,
        decimal value, decimal minimum, int cap, DateTimeOffset start, DateTimeOffset end)
    {
        actor.RequireAdmin();
        code = ShopRules.Text(code, 40, "Discount code").ToUpperInvariant();
        if (value <= 0 || (percentage && (value > 100 || decimal.Round(value, 2) != value)) || cap < 1 || end <= start)
            throw new ShopRuleException("The value, limit, or validity period is invalid.");
        if (!percentage) ShopRules.Vnd(value);
        ShopRules.Vnd(minimum);
        return store.Write(async db =>
        {
            var row = id == 0 ? new Promotion() : await db.First(db.Query<Promotion>().Where(p => p.Id == id))
                ?? throw new ShopRuleException("The discount code does not exist.");
            if (await db.Count(db.Query<Promotion>().Where(p => p.Code == code && p.Id != id)) > 0)
                throw new ShopRuleException("The discount code already exists.");
            var used = await db.Count(db.Query<PromotionClaim>().Where(c => c.PromotionId == id && c.Status != ClaimStatus.Released));
            if (cap < used) throw new ShopRuleException("The limit cannot be lower than the number of uses already recorded.");
            row.Code = code;
            row.Active = active;
            row.IsPercentage = percentage;
            row.Value = value;
            row.MinimumVnd = minimum;
            row.UsageCap = cap;
            row.StartsAt = start;
            row.EndsAt = end;
            if (id == 0) db.Add(row);
            await db.Save();
            db.Audit(actor.UserId, "Cập nhật mã giảm giá", row.Id.ToString(), time.GetUtcNow());
            return 0;
        });
    }

    internal static async Task<(Promotion? Promotion, decimal Discount)> Evaluate(IShopSession db, string code,
        string customerId, decimal subtotal, DateTimeOffset now)
    {
        if (code.Length == 0) return (null, 0);
        var row = await db.First(db.Query<Promotion>().Where(p => p.Code == code))
            ?? throw new ShopRuleException("The discount code does not exist.");
        var claims = db.Query<PromotionClaim>().Where(c => c.PromotionId == row.Id && c.Status != ClaimStatus.Released);
        if (await db.Count(claims) >= row.UsageCap || await db.Count(claims.Where(c => c.CustomerId == customerId)) > 0)
            throw new ShopRuleException("The discount code has reached its usage limit.");
        return (row, row.Discount(subtotal, now));
    }
}

public sealed class CheckoutService(IShopStore store, ShopSettings settings, TimeProvider time,
    TransportSettings? transport = null, ITransportRouting? routing = null, IShippingGateway? shipping = null)
{
    public const string CodRestrictedMessage = "Cash on Delivery is only available for single-seller HKShop orders. Prepaid SePay is required for third-party or multi-seller carts.";
    public const string ExpiredQuoteMessage = "Checkout quote has expired. Please review your order again.";
    public bool UsesTransport => shipping?.Enabled == true || transport?.Enabled == true;
    private const string RouteChanged = "The delivery route or pickup location changed. Please review your order again.";

    public Task<CheckoutPreview> Quote(ShopActor actor, long addressId, PaymentMethod method, string coupon)
    {
        actor.RequireCustomer();
        coupon = ShopRules.Text(coupon, 40, "Coupon code", false).ToUpperInvariant();
        if (!Enum.IsDefined(method)) throw new ShopRuleException("Invalid payment method.");
        return store.Read(async db =>
        {
            var now = time.GetUtcNow();
            var cart = await FindCart(db, actor.UserId, now);
            var (address, lines, sellers, subtotal, promotion, discount, shippingFee, routes) =
                await Price(db, cart, actor.UserId, addressId, method, coupon, now, quoteRoutes: true);
            var quote = new CheckoutQuote(actor.UserId, addressId, method, coupon, cart.Revision, Guid.NewGuid(),
                time.GetUtcNow().AddMinutes(10), Fingerprint(address, method, promotion?.Id, discount, shippingFee, sellers))
                { TransportRoutes = routes };
            return new CheckoutPreview(quote, address, lines, sellers, subtotal, discount, shippingFee);
        });
    }

    public Task<Guid> Place(ShopActor actor, CheckoutQuote quote) =>
        store.Write(db => PlaceInTransaction(db, actor, quote));

    public static void ValidateSubmission(ShopActor actor, CheckoutQuote quote)
    {
        actor.RequireCustomer();
        if (quote.CustomerId != actor.UserId || quote.SubmissionKey == Guid.Empty)
            throw new ShopRuleException("Invalid checkout session.");
        if (!Enum.IsDefined(quote.Method)) throw new ShopRuleException("Invalid payment method.");
    }

    public async Task<Guid> PlaceInTransaction(IShopSession db, ShopActor actor, CheckoutQuote quote)
    {
        ValidateSubmission(actor, quote);
        var existing = await db.First(db.Query<Order>().Where(o => o.CustomerId == actor.UserId && o.SubmissionKey == quote.SubmissionKey));
        if (existing is not null) return existing.Id;

        var now = time.GetUtcNow();
        if (now >= quote.ExpiresAt) throw new ShopRuleException(ExpiredQuoteMessage);
        var cart = await FindCart(db, actor.UserId, now);
        if (cart.Revision != quote.Revision) throw new ShopRuleException("Your cart changed. Please review your order again.");

        if (UsesTransport != (quote.TransportRoutes is not null)) throw new ShopRuleException(ExpiredQuoteMessage);
        var (address, lines, sellers, subtotal, promotion, discount, shippingFee, routes) =
            await Price(db, cart, actor.UserId, quote.AddressId, quote.Method, quote.Coupon, now, quote.TransportRoutes);
        if (Fingerprint(address, quote.Method, promotion?.Id, discount, shippingFee, sellers) != quote.Fingerprint)
            throw new ShopRuleException("Pricing, items, or delivery address changed. Please review your order again.");

        var total = ShopRules.Vnd(subtotal - discount + shippingFee);
        var supportsCod = sellers.Count == 1 && sellers[0].SellerId == Seller.FirstPartyId;
        if (!UsesTransport && quote.Method == PaymentMethod.SePay && total == 0 && supportsCod)
            throw new ShopRuleException("Orders totaling 0 VND do not require SePay. Select Cash on Delivery.");

        var requiresOnlineCapture = quote.Method == PaymentMethod.SePay && total > 0;
        var paymentStatus = requiresOnlineCapture
            ? PaymentStatus.Pending
            : (quote.Method != PaymentMethod.SePay ? PaymentStatus.Due : PaymentStatus.Paid);

        var offerIds = lines.Select(line => line.OfferId).ToList();
        var balances = await db.List(from balance in db.Query<InventoryBalance>()
                                     join location in db.Query<SellerLocation>()
                                         on new { Id = balance.LocationId, balance.SellerId }
                                         equals new { location.Id, location.SellerId }
                                     where offerIds.Contains(balance.OfferId) && location.Active
                                     select balance);
        var balanceByOffer = new Dictionary<long, InventoryBalance>(lines.Count);
        foreach (var line in lines)
        {
            var matches = balances.Where(b => b.OfferId == line.OfferId && b.SellerId == line.SellerId).ToList();
            if (matches.Count != 1)
                throw new ShopRuleException("Inventory location for a cart item is unavailable.");
            balanceByOffer[line.OfferId] = matches[0];
        }

        var skuIds = lines.Select(line => line.SkuId).Distinct().ToList();
        var skuSnapshots = (await db.List(from sku in db.Query<Sku>()
                                          join product in db.Query<Product>() on sku.ProductId equals product.Id
                                          where skuIds.Contains(sku.Id)
                                          select new { sku.Id, sku.Code, ProductName = product.Name, sku.Size, sku.Color }))
            .ToDictionary(x => x.Id);

        var order = new Order
        {
            SellerId = sellers.Count == 1 ? sellers[0].SellerId : null,
            CustomerId = actor.UserId,
            CustomerEmail = actor.Email,
            SubmissionKey = quote.SubmissionKey,
            CreatedAt = now,
            PaymentDeadline = requiresOnlineCapture ? now.AddMinutes(15) : null,
            PaymentStatus = paymentStatus,
            Recipient = address.Recipient,
            Phone = address.Phone,
            ShippingAddress = address.AddressText,
            ShippingVnd = shippingFee,
            TotalVnd = total,
            Payment = new Payment
            {
                Method = quote.Method,
                Status = paymentStatus,
                AmountVnd = total,
                PaidAt = !requiresOnlineCapture && quote.Method == PaymentMethod.SePay ? now : null,
                NextQueryAt = requiresOnlineCapture ? now.AddMinutes(2) : null
            }
        };
        order.Number = $"HK{order.Id:N}".ToUpperInvariant();
        order.ChangeStatus(requiresOnlineCapture ? OrderStatus.AwaitingPayment : OrderStatus.Confirmed, now);

        if (quote.Method != PaymentMethod.TransportAtDelivery) order.PaymentAttempts.Add(new PaymentAttempt
        {
            OrderId = order.Id,
            AttemptNumber = 1,
            Method = quote.Method,
            Status = paymentStatus,
            AmountVnd = total,
            CapturedAmountVnd = !requiresOnlineCapture && quote.Method == PaymentMethod.SePay ? total : 0,
            ProviderInvoice = requiresOnlineCapture ? order.Number : null,
            AppliedToOrder = !requiresOnlineCapture && quote.Method == PaymentMethod.SePay,
            CreatedAt = now,
            PaidAt = !requiresOnlineCapture && quote.Method == PaymentMethod.SePay ? now : null,
            NextQueryAt = requiresOnlineCapture ? now.AddMinutes(2) : null
        });

        var reservedEntries = new List<(OrderItem Item, InventoryBalance Balance)>(lines.Count);
        var newParcels = new List<NewTransportParcel>();
        for (var index = 0; index < sellers.Count; index++)
        {
            var sellerQuote = sellers[index];
            var sellerOrder = new SellerOrder
            {
                OrderId = order.Id,
                SellerId = sellerQuote.SellerId,
                Number = $"{order.Number}-S{index + 1}",
                SubtotalVnd = sellerQuote.SubtotalVnd,
                SellerDiscountVnd = sellerQuote.SellerDiscountVnd,
                PlatformDiscountVnd = sellerQuote.PlatformDiscountVnd,
                ShippingVnd = sellerQuote.ShippingVnd,
                TotalVnd = sellerQuote.TotalVnd,
                CommissionVnd = sellerQuote.CommissionVnd,
                CreatedAt = now
            };
            sellerOrder.ChangeStatus(requiresOnlineCapture ? OrderStatus.AwaitingPayment : OrderStatus.Confirmed, now);
            if (routes is not null)
            {
                var route = routes.Single(r => r.SellerId == sellerOrder.SellerId);
                var deliveryCode = RandomNumberGenerator.GetInt32(1_000_000).ToString("D6");
                newParcels.Add(new NewTransportParcel(
                    sellerOrder.Id, route.LocationId, route.PickupAddress,
                    route.Origin, route.Destination, route.DistanceMeters, deliveryCode));
            }
            order.SellerOrders.Add(sellerOrder);

            foreach (var line in sellerQuote.Lines)
            {
                var snap = skuSnapshots[line.SkuId];
                var variant = string.Join(" / ", new[] { snap.ProductName, snap.Size, snap.Color }
                    .Where(part => !string.IsNullOrWhiteSpace(part)));
                if (variant.Length > 300) variant = variant[..300];
                var orderItem = new OrderItem
                {
                    OrderId = order.Id,
                    SellerOrderId = sellerOrder.Id,
                    SellerId = line.SellerId,
                    SkuId = line.SkuId,
                    OfferId = line.OfferId,
                    SkuCode = snap.Code,
                    SellerSkuCode = line.Code,
                    Name = line.Name,
                    ProductAndVariantSnapshot = variant,
                    Quantity = line.Quantity,
                    UnitPriceVnd = line.Price,
                    DiscountVnd = line.SellerDiscountVnd + line.PlatformDiscountVnd,
                    SellerDiscountVnd = line.SellerDiscountVnd,
                    PlatformDiscountVnd = line.PlatformDiscountVnd,
                    CommissionBps = line.CommissionBps,
                    CommissionVnd = line.CommissionVnd
                };
                order.Items.Add(orderItem);
                reservedEntries.Add((orderItem, balanceByOffer[line.OfferId]));
            }
        }

        foreach (var (orderItem, balance) in reservedEntries.OrderBy(entry => entry.Balance.Id))
            await InventoryService.ReserveOffer(db, balance, orderItem.SkuId, order.Id, orderItem.Quantity,
                "Order reservation", actor.UserId, now);

        db.Add(order);
        await db.Save();

        if (newParcels.Count > 0 && shipping is not null)
        {
            var registered = await shipping.RegisterParcelsAsync(db, newParcels);
            foreach (var so in order.SellerOrders)
                so.Transport = registered.GetValueOrDefault(so.Id);
        }

        foreach (var (orderItem, balance) in reservedEntries)
        {
            db.Add(new StockReservation
            {
                OrderId = order.Id,
                OrderItemId = orderItem.Id,
                SkuId = orderItem.SkuId,
                OfferId = orderItem.OfferId,
                InventoryBalanceId = balance.Id,
                Quantity = orderItem.Quantity,
                Status = ReservationStatus.Held,
                ExpiresAt = order.PaymentDeadline
            });
        }

        if (promotion is not null)
            db.Add(new PromotionClaim
            {
                PromotionId = promotion.Id,
                OrderId = order.Id,
                CustomerId = actor.UserId,
                Status = requiresOnlineCapture ? ClaimStatus.Held : ClaimStatus.Redeemed
            });

        cart.Items.Clear();
        CartService.Touch(cart, now);

        db.IntegrationEvent("Order", order.Id.ToString("D"), "OrderPlaced", new
        {
            OrderId = order.Id,
            order.Number,
            SubtotalVnd = subtotal,
            DiscountVnd = discount,
            order.ShippingVnd,
            order.TotalVnd,
            PaymentMethod = quote.Method.ToString(),
            PaymentStatus = order.PaymentStatus.ToString(),
            PlacedAt = now,
            SellerOrders = order.SellerOrders.Select(so => new
            {
                SellerOrderId = so.Id,
                so.SellerId,
                so.Number,
                so.SubtotalVnd,
                so.SellerDiscountVnd,
                so.PlatformDiscountVnd,
                so.ShippingVnd,
                so.TotalVnd,
                so.CommissionVnd
            })
        }, now, $"Order:{order.Id:N}:Placed");

        if (!requiresOnlineCapture)
        {
            if (paymentStatus == PaymentStatus.Paid)
                await SettlementLedger.RecordSales(db, order.PaymentAttempts.Single(), order.SellerOrders, now);
            EmitSellerOrdersConfirmed(db, order, now);
            db.Notify(order, "confirmed", "Order confirmed", now);
        }

        return order.Id;
    }

    internal static void EmitSellerOrdersConfirmed(IShopSession db, Order order, DateTimeOffset now)
    {
        foreach (var sellerOrder in order.SellerOrders.OrderBy(so => so.SellerId))
        {
            db.IntegrationEvent("SellerOrder", sellerOrder.Id.ToString("D"), "SellerOrderConfirmed", new
            {
                SellerOrderId = sellerOrder.Id,
                sellerOrder.OrderId,
                sellerOrder.SellerId,
                sellerOrder.Number,
                sellerOrder.SubtotalVnd,
                sellerOrder.SellerDiscountVnd,
                sellerOrder.PlatformDiscountVnd,
                sellerOrder.ShippingVnd,
                sellerOrder.TotalVnd,
                sellerOrder.CommissionVnd,
                ConfirmedAt = now
            }, now, $"SellerOrder:{sellerOrder.Id:N}:Confirmed");
        }
    }

    private static async Task<ShoppingCart> FindCart(IShopSession db, string user, DateTimeOffset now) =>
        await CartService.Find(db, user, null, now) ?? throw new ShopRuleException("Your cart is empty.");

    private async Task<(CustomerAddress Address, List<CartLine> Lines, List<SellerOrderQuote> Sellers, decimal Subtotal,
        Promotion? Promotion, decimal Discount, decimal Shipping, IReadOnlyList<TransportRoute>? Routes)>
        Price(IShopSession db, ShoppingCart cart, string user, long addressId, PaymentMethod method, string code,
            DateTimeOffset now, IReadOnlyList<TransportRoute>? routes = null, bool quoteRoutes = false)
    {
        var address = await AddressService.Find(db, user, addressId);
        var lines = await CartService.ReadLines(db, cart.Id);
        if (lines.Count == 0) throw new ShopRuleException("Your cart is empty.");
        if (lines.Any(line => !line.Active || line.Quantity > line.Available))
            throw new ShopRuleException("Please update quantities or remove out-of-stock items.");
        var supportsCod = lines.All(line => line.SellerId == Seller.FirstPartyId);
        if (method == PaymentMethod.Cod && !supportsCod)
            throw new ShopRuleException(CodRestrictedMessage);
        if ((UsesTransport && method == PaymentMethod.Cod) || (!UsesTransport && method == PaymentMethod.TransportAtDelivery))
            throw new ShopRuleException("Select a payment method available for this checkout.");

        var subtotal = ShopRules.Vnd(lines.Sum(line => line.Price * line.Quantity));
        var (promotion, discount) = await PromotionService.Evaluate(db, code, user, subtotal, now);
        var flatShipping = UsesTransport ? 0 : ShopRules.Vnd(settings.FlatShippingVnd);
        var commissionBps = ShopRules.CommissionBps(settings.DefaultCommissionBps);
        var sellers = Allocate(lines, discount, flatShipping, commissionBps);
        if (UsesTransport)
        {
            routes = await PriceRoutes(db, address, lines, routes, quoteRoutes);
            for (var i = 0; i < sellers.Count; i++)
                sellers[i] = sellers[i] with { ShippingVnd = routes.Single(r => r.SellerId == sellers[i].SellerId).FeeVnd };
        }
        var shippingFee = ShopRules.Vnd(sellers.Sum(s => s.ShippingVnd));
        return (address, lines, sellers, subtotal, promotion, discount, shippingFee, routes);
    }

    private async Task<IReadOnlyList<TransportRoute>> PriceRoutes(IShopSession db, CustomerAddress address,
        List<CartLine> lines, IReadOnlyList<TransportRoute>? accepted, bool quoteRoutes)
    {
        var destination = TransportPoint.Require(address.Latitude, address.Longitude, "your delivery address");
        var offerIds = lines.Select(line => line.OfferId).ToArray();
        var origins = await db.List(from balance in db.Query<InventoryBalance>()
                                    join location in db.Query<SellerLocation>() on balance.LocationId equals location.Id
                                    where location.Active && location.SellerId == balance.SellerId && offerIds.Contains(balance.OfferId)
                                    select new { balance.OfferId, Location = location });
        var sellerOrigins = new List<SellerRouteOrigin>();
        foreach (var seller in lines.GroupBy(l => l.SellerId))
        {
            var sellerOffers = seller.Select(l => l.OfferId).ToHashSet();
            var matches = origins.Where(o => sellerOffers.Contains(o.OfferId) && o.Location.SellerId == seller.Key).ToList();
            var location = matches.FirstOrDefault()?.Location;
            if (location is null || matches.Count != sellerOffers.Count || matches.Any(m => m.Location.Id != location.Id) ||
                string.IsNullOrWhiteSpace(location.PickupAddress))
                throw new ShopRuleException("A seller does not have a confirmed storage location for this parcel.");
            sellerOrigins.Add(new SellerRouteOrigin(seller.Key, location.Id, location.PickupAddress, location.Latitude, location.Longitude));
        }

        if (shipping is not null)
            return await shipping.QuoteRoutesAsync(destination, sellerOrigins, accepted, quoteRoutes);

        var routes = new List<TransportRoute>();
        foreach (var sellerOrigin in sellerOrigins)
        {
            var origin = TransportPoint.Require(sellerOrigin.Latitude, sellerOrigin.Longitude, "the storage location");
            long meters;
            if (quoteRoutes)
                meters = await (routing ?? throw new ShopRuleException("Delivery routing is unavailable.")).Distance(origin, destination)
                    ?? throw new ShopRuleException("No road route was found from storage to your address. Check your delivery pin.");
            else
            {
                var saved = accepted?.SingleOrDefault(r => r.SellerId == sellerOrigin.SellerId);
                if (saved is null || saved.LocationId != sellerOrigin.LocationId || saved.PickupAddress != sellerOrigin.PickupAddress ||
                    saved.Origin != origin || saved.Destination != destination)
                    throw new ShopRuleException(RouteChanged);
                meters = saved.DistanceMeters;
            }
            routes.Add(new(sellerOrigin.SellerId, sellerOrigin.LocationId, sellerOrigin.PickupAddress, origin, destination, meters));
        }
        return routes;
    }

    public static List<SellerOrderQuote> Allocate(IReadOnlyList<CartLine> lines, decimal platformDiscount, decimal flatShippingVnd, int defaultCommissionBps)
    {
        ShopRules.Vnd(platformDiscount);
        ShopRules.Vnd(flatShippingVnd);
        ShopRules.CommissionBps(defaultCommissionBps);

        var ordered = lines.OrderBy(l => l.SellerId).ThenBy(l => l.SkuId).ThenBy(l => l.OfferId).ToList();
        var subtotal = ordered.Sum(l => l.Price * l.Quantity);
        if (platformDiscount > subtotal) throw new ShopRuleException("Discount exceeds order subtotal.");

        var platformAllocations = new decimal[ordered.Count];
        var remainingDiscount = platformDiscount;
        for (var i = 0; i < ordered.Count; i++)
        {
            var lineTotal = ordered[i].Price * ordered[i].Quantity;
            var share = subtotal == 0 ? 0 : decimal.Floor(platformDiscount * lineTotal / subtotal);
            platformAllocations[i] = share;
            remainingDiscount -= share;
        }
        for (var i = 0; i < ordered.Count && remainingDiscount > 0; i++)
        {
            var lineTotal = ordered[i].Price * ordered[i].Quantity;
            var extra = Math.Min(remainingDiscount, lineTotal - platformAllocations[i]);
            platformAllocations[i] += extra;
            remainingDiscount -= extra;
        }

        var indexedBySeller = ordered
            .Select((line, index) => (Line: line, LineSubtotal: line.Price * line.Quantity, PlatformDiscount: platformAllocations[index]))
            .GroupBy(entry => new { entry.Line.SellerId, entry.Line.SellerName })
            .OrderBy(group => group.Key.SellerId);

        var sellers = new List<SellerOrderQuote>();
        foreach (var group in indexedBySeller)
        {
            var groupEntries = group.ToList();
            var sellerSubtotal = ShopRules.Vnd(groupEntries.Sum(e => e.LineSubtotal));
            var sellerPlatformDiscount = ShopRules.Vnd(groupEntries.Sum(e => e.PlatformDiscount));
            var bps = group.Key.SellerId == Seller.FirstPartyId ? 0 : defaultCommissionBps;
            var sellerCommission = ShopRules.Vnd(decimal.Floor(sellerSubtotal * bps / 10_000m));

            var lineCommissions = new decimal[groupEntries.Count];
            var remainingCommission = sellerCommission;
            for (var i = 0; i < groupEntries.Count; i++)
            {
                var lineCommission = decimal.Floor(groupEntries[i].LineSubtotal * bps / 10_000m);
                lineCommissions[i] = lineCommission;
                remainingCommission -= lineCommission;
            }
            for (var i = 0; i < groupEntries.Count && remainingCommission > 0; i++)
            {
                var extra = Math.Min(remainingCommission, groupEntries[i].LineSubtotal - lineCommissions[i]);
                lineCommissions[i] += extra;
                remainingCommission -= extra;
            }

            var quoteLines = groupEntries.Select((entry, i) => new CheckoutLineQuote(
                entry.Line.OfferId,
                entry.Line.SkuId,
                entry.Line.SellerId,
                entry.Line.SellerName,
                entry.Line.Code,
                entry.Line.Slug,
                entry.Line.Name,
                entry.Line.Quantity,
                entry.Line.Available,
                entry.Line.Price,
                entry.LineSubtotal,
                0,
                entry.PlatformDiscount,
                bps,
                lineCommissions[i])).ToList();

            sellers.Add(new SellerOrderQuote(
                group.Key.SellerId,
                group.Key.SellerName,
                sellerSubtotal,
                0,
                sellerPlatformDiscount,
                flatShippingVnd,
                sellerCommission,
                quoteLines));
        }

        return sellers;
    }

    private static string Fingerprint(CustomerAddress address, PaymentMethod method, long? promotion, decimal discount,
        decimal shipping, List<SellerOrderQuote> sellers) =>
        Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new
        {
            address.Recipient,
            address.Phone,
            address.AddressText,
            method,
            promotion,
            discount,
            shipping,
            Sellers = sellers.Select(s => new
            {
                s.SellerId,
                s.SubtotalVnd,
                s.PlatformDiscountVnd,
                s.ShippingVnd,
                s.CommissionVnd,
                Lines = s.Lines.Select(l => new
                {
                    l.OfferId,
                    l.SkuId,
                    l.Code,
                    l.Name,
                    l.Quantity,
                    l.Price,
                    l.PlatformDiscountVnd,
                    l.CommissionBps,
                    l.CommissionVnd
                })
            })
        })));
}
