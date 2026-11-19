using HKShop.Application;
using HKShop.Application.Payments;
using HKShop.Contracts.Ports;
using HKShop.Domain;
using HKShop.Domain.Orders;
using HKShop.Web;
using HKShop.Web.Controllers;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace HKShop.Commerce.Api;

[Route("api/commerce/checkout")]
[Authorize(Policy = ShopPolicies.CommerceCustomer)]
public sealed class CommerceCheckoutController(
    ShopRequest request,
    ICheckoutRequestDispatcher checkout,
    CheckoutTokens tokens,
    PaymentService payments) : Controller
{
    [HttpPost("place"), EnableRateLimiting("CustomerWrites")]
    public async Task<IActionResult> Place(string quoteToken)
    {
        if (!ModelState.IsValid) throw new ShopRuleException("Invalid form data.");
        var quote = tokens.Read(quoteToken);
        if (quote.Method == PaymentMethod.SePay && !await payments.IsOnlineAvailableAsync(HttpContext.RequestAborted))
            throw new ShopRuleException("SePay Sandbox is not available.");
        var result = await checkout.Submit(await request.Actor(), quote);
        Response.Headers.Location = result.OrderId is Guid orderId
            ? $"/orders/{orderId:D}" : $"/checkout/requests/{result.RequestId:D}";
        return StatusCode(StatusCodes.Status303SeeOther);
    }
}
