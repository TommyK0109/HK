using HKShop.Application;
using HKShop.Contracts.Ports;
using HKShop.Domain;
using HKShop.Web;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace HKShop.EOffice.Operations.Api;

[Route("api/eoffice/payouts")]
[Authorize(Policy = ShopPolicies.EOfficeAdmin)]
public sealed class EOfficePayoutsController(IShopRequestContext request, ICommerceOperationsPort commerce) : Controller
{
    [HttpPost("{id:guid}/review")]
    public async Task<IActionResult> Review([FromRoute] Guid id, [FromForm] bool? approve, [FromForm] string? note)
    {
        RequireForm();
        if (approve is not bool decision) throw new ShopRuleException("Choose whether to approve or reject the payout request.");
        await commerce.ReviewPayoutAsync(await request.Actor(), id, decision, note ?? "");
        TempData["Notice"] = decision ? "Payout request approved." : "Payout request rejected.";
        return Redirect("/eoffice/payouts");
    }

    [HttpPost("{id:guid}/reconcile")]
    public async Task<IActionResult> Reconcile([FromRoute] Guid id)
    {
        RequireForm();
        await commerce.ReconcilePayoutAsync(await request.Actor(), id, HttpContext.RequestAborted);
        return Redirect("/eoffice/payouts");
    }

    private void RequireForm()
    {
        if (!ModelState.IsValid) throw new ShopRuleException("The payout information is invalid.");
    }
}
