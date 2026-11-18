using HKShop.Application;
using HKShop.Contracts.Ports;
using HKShop.Domain;
using HKShop.Web;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace HKShop.EOffice.Operations.Api;

[Route("api/eoffice/inventory")]
[Authorize(Policy = ShopPolicies.EOfficeStaff)]
public sealed class EOfficeInventoryController(IShopRequestContext request, ICatalogOperationsPort catalog) : Controller
{
    [HttpPost("stock")]
    public async Task<IActionResult> Stock(long skuId, int delta, int threshold, string reason)
    {
        if (!ModelState.IsValid) throw new ShopRuleException("Invalid form data.");
        await catalog.AdjustFirstPartyStockAsync(await request.Actor(), skuId, delta, threshold, reason);
        TempData["Notice"] = "Saved changes.";
        return Redirect($"/eoffice/inventory?sku={skuId}");
    }
}
