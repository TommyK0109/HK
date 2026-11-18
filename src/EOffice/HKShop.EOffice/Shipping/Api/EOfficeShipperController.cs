using HKShop.Application;
using HKShop.Application.Transportation;
using HKShop.Domain;
using HKShop.Domain.Orders;
using HKShop.Web;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace HKShop.EOffice.Shipping.Api;

[Authorize(Policy = ShopPolicies.EOfficeShipper)]
[Route("api/eoffice/shipper")]
public sealed class EOfficeShipperController(IShopRequestContext request, TransportService transport) : Controller
{
    [HttpPost("presence")]
    public async Task<IActionResult> Presence(bool onDuty, string? latitude, string? longitude)
    {
        CheckForm();
        var pin = onDuty ? TransportPoint.Parse(latitude, longitude, "shipper availability") : (TransportPoint?)null;
        await transport.UpdatePresence(await request.Actor(), onDuty, pin?.Latitude, pin?.Longitude);
        return NoContent();
    }

    [HttpGet("state")]
    public async Task<IActionResult> ShipperState()
    {
        var state = await transport.ShipperJob(await request.Actor());
        return Json(new { assignment = state.AssignmentId, stage = state.Job?.Parcel.Stage.ToString(), verified = state.Job?.Parcel.FundsVerifiedAt.HasValue ?? false });
    }

    [HttpPost("jobs/{id:guid}/respond")]
    public async Task<IActionResult> Respond(Guid id, Guid assignmentId, bool accept)
    {
        CheckForm();
        await transport.Respond(await request.Actor(), id, assignmentId, accept);
        return Redirect("/eoffice/shipper");
    }

    [HttpPost("jobs/{id:guid}/deliver")]
    public async Task<IActionResult> Deliver(Guid id, string code, DoorPayment? payment, decimal? cash)
    {
        CheckForm();
        if (payment is { } value && !Enum.IsDefined(value)) throw new ShopRuleException("Select a valid payment method.");
        var delivered = await transport.Deliver(await request.Actor(), id, code, payment, cash);
        TempData["Notice"] = delivered ? "Delivery confirmed." : "The delivery code was incorrect.";
        return Redirect("/eoffice/shipper");
    }

    [HttpPost("jobs/{id:guid}/return")]
    public async Task<IActionResult> Return(Guid id, string reason)
    {
        CheckForm();
        await transport.ReturnToSender(await request.Actor(), id, reason);
        return Redirect("/eoffice/shipper");
    }

    private void CheckForm()
    {
        if (!ModelState.IsValid) throw new ShopRuleException("Invalid transport form.");
    }
}
