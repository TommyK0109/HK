using HKShop.Application;
using HKShop.Application.Sellers;
using HKShop.Domain;
using HKShop.Web;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace HKShop.Commerce.Api;

[Authorize(Policy = ShopPolicies.CommerceSeller)]
[Route("api/commerce/inventory")]
public sealed class SellerInventoryController(SellerCatalogService catalog, ShopRequest request) : Controller
{
    [HttpPost("seller/{sellerId:long}/offers/{offerId:long}/stock"), EnableRateLimiting("CustomerWrites")]
    public async Task<IActionResult> AdjustStock([FromRoute] long sellerId, [FromRoute] long offerId, [FromForm] int delta, [FromForm] string reason)
    {
        if (!ModelState.IsValid) throw new ShopRuleException("The submitted information is invalid.");
        await catalog.AdjustStock(await request.Actor(), sellerId, offerId, delta, reason);
        TempData["Notice"] = "Inventory updated.";
        return LocalRedirect($"/seller/{sellerId}/catalog?history={offerId}#stock-history");
    }
}
