using HKShop.Application;
using HKShop.Application.Transportation;
using HKShop.Contracts.Ports;
using HKShop.Domain;
using HKShop.Domain.Orders;
using HKShop.Web;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace HKShop.EOffice.Shipping.Api;

[Authorize(Policy = ShopPolicies.EOfficeShipper)]
[Route("api/eoffice/shipper")]
public sealed class EOfficeShipperController(IShopRequestContext request, IShippingShipperPort shipper) : Controller
{
    [HttpPost("presence")]
    public async Task<IActionResult> Presence(bool onDuty, string? latitude, string? longitude)
    {
        CheckForm();
        var pin = onDuty ? TransportPoint.Parse(latitude, longitude, "shipper availability") : (TransportPoint?)null;
        await shipper.UpdatePresenceAsync(await request.Actor(), onDuty, pin?.Latitude, pin?.Longitude, HttpContext.RequestAborted);
        return NoContent();
    }

    [HttpGet("state")]
    public async Task<IActionResult> ShipperState()
    {
        var state = await shipper.ShipperStateAsync(await request.Actor(), HttpContext.RequestAborted);
        return Json(new { assignment = state.AssignmentId, stage = state.ActiveJob?.Parcel.Stage.ToString(), verified = state.ActiveJob?.Parcel.FundsVerifiedAt.HasValue ?? false });
    }

    [HttpPost("jobs/{id:guid}/respond")]
    public async Task<IActionResult> Respond(Guid id, Guid assignmentId, bool accept)
    {
        CheckForm();
        await shipper.RespondAsync(await request.Actor(), id, assignmentId, accept, HttpContext.RequestAborted);
        return Redirect("/eoffice/shipper");
    }

    [HttpPost("jobs/{id:guid}/deliver")]
    public async Task<IActionResult> Deliver(Guid id, string code, DoorPayment? payment, decimal? cash)
    {
        CheckForm();
        if (payment is { } value && !Enum.IsDefined(value)) throw new ShopRuleException("Select a valid payment method.");
        var delivered = await shipper.DeliverAsync(await request.Actor(), id, code, payment, cash, HttpContext.RequestAborted);
        TempData["Notice"] = delivered ? "Delivery confirmed." : "The delivery code was incorrect.";
        return Redirect("/eoffice/shipper");
    }

    [HttpPost("jobs/{id:guid}/return")]
    public async Task<IActionResult> Return(Guid id, string reason)
    {
        CheckForm();
        await shipper.ReturnToSenderAsync(await request.Actor(), id, reason, HttpContext.RequestAborted);
        return Redirect("/eoffice/shipper");
    }

    private void CheckForm()
    {
        if (!ModelState.IsValid) throw new ShopRuleException("Invalid transport form.");
    }
}
