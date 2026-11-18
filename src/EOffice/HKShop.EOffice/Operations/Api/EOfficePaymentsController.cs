using HKShop.Application;
using HKShop.Contracts.Ports;
using HKShop.Domain;
using HKShop.Web;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace HKShop.EOffice.Operations.Api;

[Route("api/eoffice/payments")]
[Authorize(Policy = ShopPolicies.EOfficeAdmin)]
public sealed class EOfficePaymentsController(IShopRequestContext request, ICommerceOperationsPort commerce) : Controller
{
    [HttpPost("reconcile")]
    public async Task<IActionResult> Reconcile(Guid id)
    {
        RequireForm();
        try { await commerce.ReconcilePaymentAsync(await request.Actor(), id, HttpContext.RequestAborted); }
        catch (Exception error) when (error is HttpRequestException or TaskCanceledException)
        { throw new ShopRuleException("Cannot reconcile transaction from SePay."); }
        TempData["Notice"] = "Saved changes.";
        return Redirect("/eoffice/payments");
    }

    [HttpPost("refund")]
    public async Task<IActionResult> Refund(long id, string reference)
    {
        RequireForm();
        await commerce.CompleteRefundAsync(await request.Actor(), id, reference);
        TempData["Notice"] = "Saved changes.";
        return Redirect("/eoffice/payments");
    }

    private void RequireForm()
    {
        if (!ModelState.IsValid) throw new ShopRuleException("Invalid form data.");
    }
}
