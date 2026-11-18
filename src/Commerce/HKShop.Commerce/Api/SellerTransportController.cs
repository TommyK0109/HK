using HKShop.Application;
using HKShop.Application.Transportation;
using HKShop.Contracts.Ports;
using HKShop.Domain;
using HKShop.Web;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace HKShop.Commerce.Api;

[Authorize(Policy = ShopPolicies.CommerceSeller)]
[Route("api/commerce/seller/{sellerId:long}/transport")]
public sealed class SellerTransportController(ShopRequest request, IShippingGateway shipping) : Controller
{
    [HttpPost("location")]
    public async Task<IActionResult> Location(long sellerId, string address, string? latitude, string? longitude)
    {
        if (!ModelState.IsValid) throw new ShopRuleException("Invalid transport form.");
        var pin = TransportPoint.Parse(latitude, longitude, "pickup");
        var actor = await request.Actor();
        await shipping.SetSellerLocationAsync(actor, sellerId, address, pin.Latitude, pin.Longitude);
        TempData["Notice"] = "Pickup location saved.";
        return Redirect($"/seller/{sellerId}/transport");
    }

    [HttpPost("orders/{id:guid}/packed")]
    public async Task<IActionResult> Packed(long sellerId, Guid id)
    {
        await shipping.MarkParcelPackedAsync(await request.Actor(), sellerId, id);
        return Redirect($"/seller/{sellerId}/orders/{id:D}");
    }

    [HttpPost("orders/{id:guid}/pickup")]
    public async Task<IActionResult> Pickup(long sellerId, Guid id)
    {
        await shipping.RequestParcelPickupAsync(await request.Actor(), sellerId, id);
        return Redirect($"/seller/{sellerId}/orders/{id:D}");
    }

    [HttpPost("orders/{id:guid}/handoff")]
    public async Task<IActionResult> Handoff(long sellerId, Guid id, string code)
    {
        if (!ModelState.IsValid) throw new ShopRuleException("Invalid transport form.");
        await shipping.ConfirmParcelHandoffAsync(await request.Actor(), sellerId, id, code);
        return Redirect($"/seller/{sellerId}/orders/{id:D}");
    }
}
