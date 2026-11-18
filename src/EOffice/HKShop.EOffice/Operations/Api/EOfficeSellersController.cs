using HKShop.Application;
using HKShop.Contracts.Ports;
using HKShop.Domain;
using HKShop.Domain.Sellers;
using HKShop.Web;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace HKShop.EOffice.Operations.Api;

[Route("api/eoffice/sellers")]
[Authorize(Policy = ShopPolicies.EOfficeAdmin)]
public sealed class EOfficeSellersController(IShopRequestContext request, ICommerceOperationsPort commerce) : Controller
{
    [HttpPost("{sellerId:long}/status")]
    public async Task<IActionResult> SetStatus([FromRoute] long sellerId, [FromForm] SellerStatus status, [FromForm] string? reason)
    {
        if (!ModelState.IsValid || !Enum.IsDefined(status)) throw new ShopRuleException("The status is invalid.");
        await commerce.SetSellerStatusAsync(await request.Actor(), sellerId, status, reason);
        TempData["Notice"] = "Store status updated.";
        return Redirect("/eoffice/sellers");
    }
}
