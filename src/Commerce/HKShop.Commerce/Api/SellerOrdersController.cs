using HKShop.Application;
using HKShop.Application.Orders;
using HKShop.Domain;
using HKShop.Web;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace HKShop.Commerce.Api;

[Authorize(Policy = ShopPolicies.CommerceSeller)]
[Route("api/commerce/seller/{sellerId:long}/orders/{id:guid}")]
public sealed class SellerOrdersController(ShopRequest request, OrderService orders, ReturnService returns) : Controller
{
    [HttpPost("pack")]
    public Task<IActionResult> Pack([FromRoute] long sellerId, [FromRoute] Guid id) =>
        Update(sellerId, id, actor => orders.StartPackingSellerOrder(actor, id, sellerId));

    [HttpPost("cancel")]
    public Task<IActionResult> Cancel([FromRoute] long sellerId, [FromRoute] Guid id, string reason = "") =>
        Update(sellerId, id, actor => orders.CancelSellerOrder(actor, id, reason, sellerId));

    [HttpPost("dispatch")]
    public Task<IActionResult> Dispatch([FromRoute] long sellerId, [FromRoute] Guid id, string carrier, string tracking) =>
        Update(sellerId, id, actor => orders.DispatchSellerOrder(actor, id, carrier, tracking, sellerId));

    [HttpPost("deliver")]
    public Task<IActionResult> Deliver([FromRoute] long sellerId, [FromRoute] Guid id, decimal? collected, string reference = "") =>
        Update(sellerId, id, actor => orders.DeliverSellerOrder(actor, id, collected, reference, sellerId));

    [HttpPost("exception")]
    public Task<IActionResult> ExceptionNote([FromRoute] long sellerId, [FromRoute] Guid id, string note) =>
        Update(sellerId, id, actor => orders.DeliveryExceptionSellerOrder(actor, id, note, sellerId));

    [HttpPost("undeliverable")]
    public Task<IActionResult> Undeliverable([FromRoute] long sellerId, [FromRoute] Guid id, string reason, long[] itemId, int[] quantity)
    {
        if (itemId.Length != quantity.Length) throw new ShopRuleException("Invalid product data.");
        var counts = itemId.Zip(quantity, (item, count) => new ReturnQuantity(item, count)).ToList();
        return Update(sellerId, id, actor => orders.ReceiveUndeliveredSellerOrder(actor, id, reason, counts, sellerId));
    }

    [HttpPost("/api/commerce/seller/{sellerId:long}/returns/{returnId:guid}/review")]
    public Task<IActionResult> Review([FromRoute] long sellerId, [FromRoute] Guid returnId, bool approved, string note = "") =>
        Update(sellerId, null, actor => returns.Review(actor, sellerId, returnId, approved, note));

    [HttpPost("/api/commerce/seller/{sellerId:long}/returns/{returnId:guid}/inspect")]
    public Task<IActionResult> Inspect([FromRoute] long sellerId, [FromRoute] Guid returnId, string note,
        long[] itemId, int[] accepted, int[] restocked)
    {
        if (itemId.Length != accepted.Length || itemId.Length != restocked.Length)
            throw new ShopRuleException("Invalid product data.");
        var items = itemId.Select((item, index) => new InspectedItem(item, accepted[index], restocked[index])).ToList();
        return Update(sellerId, null, actor => returns.Inspect(actor, sellerId, returnId, items, note));
    }

    private async Task<IActionResult> Update(long sellerId, Guid? sellerOrderId, Func<ShopActor, Task<Guid>> action)
    {
        if (!ModelState.IsValid) throw new ShopRuleException("Invalid form data.");
        var orderId = await action(await request.Actor());
        TempData["Notice"] = "Order updated.";
        return Redirect($"/seller/{sellerId}/orders/{(sellerOrderId ?? orderId):D}");
    }
}
