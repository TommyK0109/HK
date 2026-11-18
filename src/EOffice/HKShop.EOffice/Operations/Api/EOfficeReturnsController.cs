using HKShop.Application;
using HKShop.Application.Orders;
using HKShop.Contracts.Ports;
using HKShop.Domain;
using HKShop.Web;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace HKShop.EOffice.Operations.Api;

[Route("api/eoffice/returns")]
[Authorize(Policy = ShopPolicies.EOfficeStaff)]
public sealed class EOfficeReturnsController(IShopRequestContext request, ICommerceOperationsPort commerce) : Controller
{
    [HttpPost("{id:guid}/review")]
    public async Task<IActionResult> Review(Guid id, bool approved, string note = "")
    {
        RequireForm();
        var orderId = await commerce.ReviewReturnAsync(await request.Actor(), id, approved, note);
        TempData["Notice"] = "Saved changes.";
        return Redirect($"/eoffice/orders/{orderId:D}");
    }

    [HttpPost("{id:guid}/inspect")]
    public async Task<IActionResult> Inspect(Guid id, string note, long[] itemId, int[] accepted, int[] restocked)
    {
        RequireForm();
        if (itemId.Length != accepted.Length || itemId.Length != restocked.Length)
            throw new ShopRuleException("Invalid product data.");
        var items = itemId.Select((item, index) => new InspectedItem(item, accepted[index], restocked[index])).ToList();
        var orderId = await commerce.InspectReturnAsync(await request.Actor(), id, items, note);
        TempData["Notice"] = "Saved changes.";
        return Redirect($"/eoffice/orders/{orderId:D}");
    }

    private void RequireForm()
    {
        if (!ModelState.IsValid) throw new ShopRuleException("Invalid form data.");
    }
}
