using HKShop.Application;
using HKShop.Application.Settlement;
using HKShop.Domain;
using HKShop.Web;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace HKShop.Commerce.Api;

[Authorize(Policy = ShopPolicies.CommerceSeller)]
[Route("api/commerce/seller/{sellerId:long}/payouts")]
public sealed class SellerPayoutsController(ShopRequest request, PayoutService payouts) : Controller
{
    [HttpPost("")]
    public async Task<IActionResult> RequestPayout([FromRoute] long sellerId, [FromForm] Guid submissionKey, [FromForm] decimal amount)
    {
        if (!ModelState.IsValid) throw new ShopRuleException("The payout information is invalid.");
        var id = await payouts.Request(await request.Actor(), sellerId, submissionKey, amount);
        TempData["Notice"] = "Payout request submitted.";
        return Redirect($"/seller/{sellerId}/settlement#payout-{id:D}");
    }
}
