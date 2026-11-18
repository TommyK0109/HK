using System.Globalization;
using HKShop.Application;
using HKShop.Application.Sellers;
using HKShop.Domain;
using HKShop.Web;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace HKShop.Commerce.Api;

[Authorize(Policy = ShopPolicies.CommerceSeller)]
[Route("api/commerce/seller/{sellerId:long}/catalog")]
public sealed class SellerCatalogController(SellerCatalogService catalog, ShopRequest request) : Controller
{
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
