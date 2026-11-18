using System.Globalization;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using HKShop.Domain;
using HKShop.Domain.Orders;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.WebUtilities;

namespace HKShop.Application
{
    public interface IShopSession
    {
        IQueryable<T> Query<T>() where T : class;
        Task<List<T>> List<T>(IQueryable<T> query);
        Task<T?> First<T>(IQueryable<T> query);
        Task<int> Count<T>(IQueryable<T> query);
        Task<decimal> Sum(IQueryable<decimal> query);
        void Add<T>(T entity) where T : class;
        void Remove<T>(T entity) where T : class;
        Task Save();
    }

    public interface IShopStore
    {
        Task<T> Read<T>(Func<IShopSession, Task<T>> query);
        Task<T> Write<T>(Func<IShopSession, Task<T>> action);
    }

    public sealed class ShopSettings
    {
        public decimal FlatShippingVnd { get; set; } = 30_000;
        public int DefaultCommissionBps { get; set; } = 1000;
        public int SettlementHoldDays { get; set; } = 14;
        public string PublicOrigin { get; set; } = "";
        public string MediaPath { get; set; } = "data/media";
        public string KeysPath { get; set; } = "data/keys";
        public static DateTimeOffset VietnamTime(DateTimeOffset instant) => instant.ToOffset(TimeSpan.FromHours(7));
    }

    public sealed record Page<T>(IReadOnlyList<T> Items, int Number, int Total, int Size)
    {
        public int Last => Math.Max(1, (int)Math.Ceiling(Total / (double)Size));
    }
}

namespace HKShop.Application.Catalog
{
    public sealed class ProductImageContent(Stream content, string contentType, IDisposable? owner = null) : IAsyncDisposable
    {
        public Stream Content { get; } = content;
        public string ContentType { get; } = contentType;

        public async ValueTask DisposeAsync()
        {
            await Content.DisposeAsync();
            owner?.Dispose();
        }
    }

    public interface IProductImageStore
    {
        Task<string> Save(Stream source);
        Task Delete(string name) => Task.CompletedTask;
        Task<ProductImageContent?> OpenRead(string name, CancellationToken cancellationToken = default) => Task.FromResult<ProductImageContent?>(null);
        string Url(string name) => $"/media/{Uri.EscapeDataString(name)}";
    }
}

namespace HKShop.Application.Transportation
{
    public sealed class TransportSettings
    {
        public bool Enabled { get; set; }
        public string RoutingBaseUrl { get; set; } = "";
        public string BankAccount { get; set; } = "";
        public string BankName { get; set; } = "";
        public string BankWebhookKey { get; set; } = "";
    }

    public readonly record struct TransportPoint(double Latitude, double Longitude)
    {
        public static TransportPoint Parse(string? latitude, string? longitude, string label) =>
            Require(ParseNumber(latitude), ParseNumber(longitude), label);

        private static double? ParseNumber(string? number) =>
            double.TryParse(number, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) ? parsed : null;

        public static TransportPoint Require(double? latitude, double? longitude, string label)
        {
            if (latitude is not double lat || longitude is not double lng ||
                !double.IsFinite(lat) || !double.IsFinite(lng) || lat is < -90 or > 90 || lng is < -180 or > 180)
                throw new ShopRuleException($"Set a valid location pin for {label}.");
            return new(lat, lng);
        }

        public double DistanceTo(TransportPoint other)
        {
            const double radians = Math.PI / 180;
            var lat = (other.Latitude - Latitude) * radians;
            var lng = (other.Longitude - Longitude) * radians;
            var a = Math.Pow(Math.Sin(lat / 2), 2) + Math.Cos(Latitude * radians) *
                Math.Cos(other.Latitude * radians) * Math.Pow(Math.Sin(lng / 2), 2);
            return 12_742_000 * Math.Asin(Math.Sqrt(Math.Clamp(a, 0, 1)));
        }
    }

    public sealed record TransportRoute(long SellerId, long LocationId, string PickupAddress,
        TransportPoint Origin, TransportPoint Destination, long DistanceMeters)
    {
        public decimal FeeVnd => ShopRules.Vnd(DistanceMeters * 5m);
    }

    public interface ITransportRouting
    {
        Task<long?> Distance(TransportPoint origin, TransportPoint destination, CancellationToken token = default);
    }
}

namespace HKShop.Application.Checkout
{
    using HKShop.Application.Transportation;

    public sealed record CheckoutQuote(string CustomerId, long AddressId, PaymentMethod Method, string Coupon,
        Guid Revision, Guid SubmissionKey, DateTimeOffset ExpiresAt, string Fingerprint)
    {
        public IReadOnlyList<TransportRoute>? TransportRoutes { get; init; }
    }
}

namespace HKShop.Web.Controllers
{
    using HKShop.Application.Checkout;

    public sealed class CheckoutTokens(IDataProtectionProvider protection)
    {
        private readonly IDataProtector protector = protection.CreateProtector("HKShop.CheckoutQuote.v1");

        public string Protect(CheckoutQuote quote) => protector.Protect(JsonSerializer.Serialize(quote));

        public CheckoutQuote Read(string token)
        {
            if (string.IsNullOrWhiteSpace(token) || token.Length > 256 * 1024)
                throw new ShopRuleException("Invalid checkout quote.");
            try
            {
                return JsonSerializer.Deserialize<CheckoutQuote>(protector.Unprotect(token))
                    ?? throw new ShopRuleException("Invalid checkout quote.");
            }
            catch (Exception exception) when (exception is CryptographicException or JsonException)
            { throw new ShopRuleException("Invalid checkout quote. Please check your shopping cart."); }
        }
    }
}

namespace HKShop.Web
{
    using HKShop.Application;
    using Microsoft.AspNetCore.Mvc.ViewFeatures;
    using Microsoft.Extensions.DependencyInjection;

    public sealed record ShopUserIdentity(
        string Id,
        string? Email,
        string DisplayName,
        string? ContactPhone,
        bool EmailConfirmed,
        bool TwoFactorEnabled,
        bool Disabled);

    public interface IShopUserResolver
    {
        string? GetUserId(ClaimsPrincipal principal);
        Task<ShopUserIdentity?> GetIdentityAsync(ClaimsPrincipal principal);
        Task<ShopActor> LoadActorAsync(ClaimsPrincipal principal);
    }

    public interface IShopRequestContext
    {
        HttpContext Context { get; }
        string? UserId { get; }
        Task<ShopUserIdentity?> Identity();
        Task<ShopActor> Actor();
        string? GuestHash(bool create = false);
        void ForgetGuest();
        string? Flash(string key);
    }

    public sealed class ShopRequest(IHttpContextAccessor accessor, IShopUserResolver users) : IShopRequestContext
    {
        private const string CartCookie = "hkshop-cart";
        private Task<ShopActor>? actor;
        private Task<ShopUserIdentity?>? identity;

        public HttpContext Context => accessor.HttpContext ?? throw new InvalidOperationException("HTTP request required.");
        public string? UserId => users.GetUserId(Context.User);

        public Task<ShopUserIdentity?> Identity() => identity ??= users.GetIdentityAsync(Context.User);
        public Task<ShopActor> Actor() => actor ??= users.LoadActorAsync(Context.User);

        public string? Flash(string key) =>
            Context.RequestServices.GetService<ITempDataDictionaryFactory>()?.GetTempData(Context)[key] as string;

        public string? GuestHash(bool create = false)
        {
            var value = Context.Request.Cookies[CartCookie];
            if (value?.Length != 43 || value.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not ('-' or '_')))
                value = null;
            if (create)
            {
                value ??= WebEncoders.Base64UrlEncode(RandomNumberGenerator.GetBytes(32));
                Context.Response.Cookies.Append(CartCookie, value, new CookieOptions
                {
                    HttpOnly = true, Secure = true, SameSite = SameSiteMode.Lax, IsEssential = true,
                    MaxAge = TimeSpan.FromDays(30), Path = "/"
                });
            }
            return value is null ? null : Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
        }

        public void ForgetGuest() => Context.Response.Cookies.Delete(CartCookie, new CookieOptions { Secure = true, Path = "/" });
    }
}
