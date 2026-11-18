using HKShop.Application;
using HKShop.Application.Orders;
using HKShop.Contracts.Ports;
using HKShop.Domain;
using HKShop.Web;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace HKShop.EOffice.Operations.Api;

[Route("api/eoffice/orders")]
[Authorize(Policy = ShopPolicies.EOfficeStaff)]
public sealed class EOfficeOrdersController(IShopRequestContext request, ICommerceOperationsPort commerce) : Controller
{
    [HttpPost("{id:guid}/pack")]
    public Task<IActionResult> Pack(Guid id, Guid? sellerOrderId) =>
        UpdateFulfillment(id, sellerOrderId, (actor, child) => commerce.StartPackingSellerOrderAsync(actor, child),
            actor => commerce.StartPackingOrderAsync(actor, id));

    [HttpPost("{id:guid}/cancel")]
    public Task<IActionResult> Cancel(Guid id, Guid? sellerOrderId, string reason = "") =>
        UpdateFulfillment(id, sellerOrderId, (actor, child) => commerce.CancelSellerOrderAsync(actor, child, reason),
            actor => commerce.CancelOrderAsync(actor, id, reason));

    [HttpPost("{id:guid}/dispatch")]
    public Task<IActionResult> Dispatch(Guid id, Guid? sellerOrderId, string carrier, string tracking) =>
        UpdateFulfillment(id, sellerOrderId, (actor, child) => commerce.DispatchSellerOrderAsync(actor, child, carrier, tracking),
            actor => commerce.DispatchOrderAsync(actor, id, carrier, tracking));

    [HttpPost("{id:guid}/deliver")]
    public Task<IActionResult> Deliver(Guid id, Guid? sellerOrderId, string reference = "", decimal? collected = null) =>
        UpdateFulfillment(id, sellerOrderId, (actor, child) => commerce.DeliverSellerOrderAsync(actor, child, collected, reference),
            actor => commerce.DeliverOrderAsync(actor, id, collected, reference));

    [HttpPost("{id:guid}/exception")]
    public Task<IActionResult> ExceptionNote(Guid id, Guid? sellerOrderId, string note) =>
        UpdateFulfillment(id, sellerOrderId, (actor, child) => commerce.DeliveryExceptionSellerOrderAsync(actor, child, note),
            actor => commerce.DeliveryExceptionOrderAsync(actor, id, note));

    [HttpPost("{id:guid}/undeliverable")]
    public async Task<IActionResult> Undeliverable(Guid id, Guid? sellerOrderId, string reason, long[] itemId, int[] quantity)
    {
        RequireForm();
        if (itemId.Length != quantity.Length) throw new ShopRuleException("Invalid product data.");
        var counts = itemId.Zip(quantity, (item, count) => new ReturnQuantity(item, count)).ToList();
        return await UpdateFulfillment(id, sellerOrderId,
            (actor, child) => commerce.ReceiveUndeliveredSellerOrderAsync(actor, child, reason, counts),
            actor => commerce.ReceiveUndeliveredOrderAsync(actor, id, reason, counts));
    }

    private async Task<IActionResult> UpdateFulfillment(Guid id, Guid? sellerOrderId,
        Func<ShopActor, Guid, Task<Guid>> childAction, Func<ShopActor, Task<int>> orderAction)
    {
        RequireForm();
        var actor = await request.Actor();
        if (sellerOrderId is Guid child)
        {
            var parentId = await childAction(actor, child);
            return Updated($"/eoffice/orders/{parentId:D}");
        }
        await orderAction(actor);
        return Updated($"/eoffice/orders/{id:D}");
    }

    private IActionResult Updated(string path)
    {
        TempData["Notice"] = "Saved changes.";
        return Redirect(path);
    }

    private void RequireForm()
    {
        if (!ModelState.IsValid) throw new ShopRuleException("Invalid form data.");
    }
}
