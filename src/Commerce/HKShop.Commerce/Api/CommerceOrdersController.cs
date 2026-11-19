using HKShop.Application;
using HKShop.Application.Orders;
using HKShop.Application.Payments;
using HKShop.Domain;
using HKShop.Web;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace HKShop.Commerce.Api;

[Route("api/commerce/orders")]
[Authorize(Policy = ShopPolicies.CommerceCustomer)]
public sealed class CommerceOrdersController(
    ShopRequest request,
    OrderService orders,
    PaymentService payments,
    ReturnService returns) : Controller
{
    [HttpPost("cancel")]
    public async Task<IActionResult> Cancel(Guid id, string reason = "")
    {
        await orders.Cancel(await request.Actor(), id, reason);
        TempData["Notice"] = "Your order has been canceled.";
        return Redirect($"/orders/{id:D}");
    }

    [HttpPost("payment-retry"), EnableRateLimiting("CustomerWrites")]
    public async Task<IActionResult> RetryPayment(Guid id)
    {
        if (!ModelState.IsValid) throw new ShopRuleException("Invalid form data.");
        if (!await payments.IsOnlineAvailableAsync(HttpContext.RequestAborted))
            throw new ShopRuleException("SePay Sandbox is not available.");
        await payments.CreateRetryAttempt(await request.Actor(), id);
        TempData["Notice"] = "Created a new SePay payment attempt.";
        return Redirect($"/orders/{id:D}");
    }

    [HttpPost("return")]
    public async Task<IActionResult> RequestReturn(Guid id, Guid? sellerOrderId, string reason, long[] itemId, int[] quantity)
    {
        if (!ModelState.IsValid) throw new ShopRuleException("Invalid form data.");
        if (itemId.Length != quantity.Length) throw new ShopRuleException("Invalid product data.");
        var actor = await request.Actor();
        var items = itemId.Zip(quantity, (item, count) => new ReturnQuantity(item, count)).ToList();
        if (sellerOrderId is Guid child) await returns.Request(actor, id, child, reason, items);
        else await returns.Request(actor, id, reason, items);
        TempData["Notice"] = "Return request submitted.";
        return Redirect($"/orders/{id:D}");
    }
}
