using HKShop.Application;
using HKShop.Application.Transportation;
using HKShop.Contracts.Ports;
using HKShop.Domain;
using HKShop.Web;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace HKShop.EOffice.Shipping.Api;

[Authorize(Policy = ShopPolicies.EOfficeStaff)]
[Route("api/eoffice/transport")]
public sealed class EOfficeTransportController(
    IShopRequestContext request,
    IShippingAdminPort shippingAdmin,
    IShippingGateway shippingGateway,
    IEOfficeAuthService auth) : Controller
{
    [Authorize(Policy = ShopPolicies.EOfficeAdmin)]
    [HttpPost("shippers")]
    public async Task<IActionResult> Shipper(string email, bool active)
    {
        CheckForm();
        var (userId, confirmedEmail) = await auth.LookupConfirmedUserForShipperAsync(ShopRules.Text(email, 256, "Shipper email"));
        await shippingAdmin.ConfigureShipperAsync(await request.Actor(), userId, confirmedEmail, active);
        await auth.SyncShipperRoleAsync(userId, active);
        TempData["Notice"] = "Shipper access updated.";
        return Redirect("/eoffice/transport");
    }

    [Authorize(Policy = ShopPolicies.EOfficeAdmin)]
    [HttpPost("location")]
    public async Task<IActionResult> Location(string address, string? latitude, string? longitude, long sellerId = 1)
    {
        CheckForm();
        var pin = TransportPoint.Parse(latitude, longitude, "pickup");
        var actor = await request.Actor();
        await shippingGateway.SetSellerLocationAsync(actor, sellerId, address, pin.Latitude, pin.Longitude);
        TempData["Notice"] = "Pickup location saved.";
        return Redirect("/eoffice/transport");
    }

    [HttpPost("sellers/{sellerId:long}/orders/{id:guid}/packed")]
    public async Task<IActionResult> Packed(long sellerId, Guid id)
    {
        var orderId = await shippingGateway.MarkParcelPackedAsync(await request.Actor(), sellerId, id);
        return Redirect($"/eoffice/orders/{orderId:D}");
    }

    [HttpPost("sellers/{sellerId:long}/orders/{id:guid}/pickup")]
    public async Task<IActionResult> Pickup(long sellerId, Guid id)
    {
        var orderId = await shippingGateway.RequestParcelPickupAsync(await request.Actor(), sellerId, id);
        return Redirect($"/eoffice/orders/{orderId:D}");
    }

    [HttpPost("sellers/{sellerId:long}/orders/{id:guid}/handoff")]
    public async Task<IActionResult> Handoff(long sellerId, Guid id, string code)
    {
        var orderId = await shippingGateway.ConfirmParcelHandoffAsync(await request.Actor(), sellerId, id, code);
        return Redirect($"/eoffice/orders/{orderId:D}");
    }

    [HttpPost("jobs/{id:guid}/reassign")]
    public async Task<IActionResult> Reassign(Guid id)
    {
        await shippingAdmin.ReassignAsync(await request.Actor(), id);
        return Redirect("/eoffice/transport");
    }

    [HttpPost("jobs/{id:guid}/reset-confirmation")]
    public async Task<IActionResult> Reset(Guid id)
    {
        await shippingAdmin.ResetDeliveryConfirmationAsync(await request.Actor(), id);
        return Redirect("/eoffice/transport");
    }

    [Authorize(Policy = ShopPolicies.EOfficeAdmin)]
    [HttpPost("jobs/{id:guid}/payout")]
    public async Task<IActionResult> Payout(Guid id, string reference)
    {
        CheckForm();
        await shippingAdmin.RecordShipperPayoutAsync(await request.Actor(), id, reference);
        return Redirect("/eoffice/transport");
    }

    [Authorize(Policy = ShopPolicies.EOfficeAdmin)]
    [HttpPost("bank/{id}/resolve")]
    public async Task<IActionResult> Resolve(string id, string? reference, string? resolution)
    {
        CheckForm();
        await shippingAdmin.ResolveBankReceiptAsync(await request.Actor(), id, reference, resolution);
        return Redirect("/eoffice/transport");
    }

    private void CheckForm()
    {
        if (!ModelState.IsValid) throw new ShopRuleException("Invalid transport form.");
    }
}
