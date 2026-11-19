using System.Globalization;
using HKShop.Application;
using HKShop.Application.Catalog;
using HKShop.Application.Sellers;
using HKShop.Domain;
using HKShop.Web;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace HKShop.Commerce.Api;

[Authorize(Policy = ShopPolicies.CommerceSeller)]
[Route("api/commerce/seller/{sellerId:long}/catalog")]
public sealed class SellerCatalogController(SellerCatalogService catalog, ShopRequest request) : Controller
{
    private const int MaxPhotoBytes = 5 * 1024 * 1024;

    [HttpPost("products"), RequestSizeLimit(4 * MaxPhotoBytes + 131_072), EnableRateLimiting("CustomerWrites")]
    public async Task<IActionResult> SubmitProduct([FromRoute] long sellerId, [FromForm] string name,
        [FromForm] string? description, [FromForm] long categoryId, [FromForm] List<ProductVariantForm> variants,
        List<IFormFile> photos, [FromServices] IProductImageStore media)
    {
        RequireForm();
        if (photos.Count is < 1 or > 4 || photos.Any(photo => photo.Length is <= 0 or > MaxPhotoBytes))
            throw new ShopRuleException("Upload 1 to 4 product photos, each up to 5 MB.");
        var rows = variants.Select(row => new SellerProductVariant(row.Code, row.Size, row.Color, ParsePrice(row.Price), row.Quantity)).ToList();
        var streams = photos.Select(photo => (Func<Stream>)photo.OpenReadStream).ToList();
        await catalog.SubmitProduct(await request.Actor(), sellerId, name, description, categoryId, rows, streams, media);
        return Updated(SellerPath(sellerId), "Product and offers submitted to EOffice for review.");
    }

    public sealed class ProductVariantForm
    {
        public string Code { get; set; } = "";
        public string? Size { get; set; }
        public string? Color { get; set; }
        public string Price { get; set; } = "";
        public int Quantity { get; set; }
    }
    [HttpPost("offers"), EnableRateLimiting("CustomerWrites")]
    public async Task<IActionResult> CreateOffer([FromRoute] long sellerId, [FromForm] long skuId, [FromForm] string code,
        [FromForm] string price, [FromForm] int quantity = 0)
    {
        RequireForm();
        await catalog.CreateOffer(await request.Actor(), sellerId, skuId, code, ParsePrice(price), quantity);
        return Updated(SellerPath(sellerId), "Offer submitted to HKShop for review.");
    }

    [HttpPost("offers/{offerId:long}"), EnableRateLimiting("CustomerWrites")]
    public async Task<IActionResult> UpdateOffer([FromRoute] long sellerId, [FromRoute] long offerId, [FromForm] string code, [FromForm] string price)
    {
        RequireForm();
        var resubmitted = await catalog.UpdateOffer(await request.Actor(), sellerId, offerId, code, ParsePrice(price));
        return Updated(SellerPath(sellerId), resubmitted ? "Offer resubmitted for review." : "Offer updated.");
    }

    [HttpPost("offers/{offerId:long}/archive")]
    public async Task<IActionResult> ArchiveOffer([FromRoute] long sellerId, [FromRoute] long offerId)
    {
        await catalog.ArchiveOffer(await request.Actor(), sellerId, offerId);
        return Updated(SellerPath(sellerId), "Offer archived.");
    }

    [HttpPost("proposals"), EnableRateLimiting("CustomerWrites")]
    public async Task<IActionResult> ProposeProduct([FromRoute] long sellerId, [FromForm] string name, [FromForm] string slug,
        [FromForm] string? description, [FromForm] long categoryId)
    {
        RequireForm();
        await catalog.ProposeProduct(await request.Actor(), sellerId, name, slug, description, categoryId);
        return Updated(SellerPath(sellerId), "Product proposal submitted.");
    }

    private void RequireForm()
    {
        if (!ModelState.IsValid) throw new ShopRuleException("The submitted information is invalid.");
    }

    private static decimal ParsePrice(string price) =>
        decimal.TryParse(price, NumberStyles.None, CultureInfo.InvariantCulture, out var value)
            ? value : throw new ShopRuleException("The price must be a whole number in VND.");

    private IActionResult Updated(string path, string message)
    {
        TempData["Notice"] = message;
        return LocalRedirect(path);
    }

    private static string SellerPath(long sellerId) => $"/seller/{sellerId}/catalog";
}
