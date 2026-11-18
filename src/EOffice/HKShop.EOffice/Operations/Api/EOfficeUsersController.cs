using HKShop.Application;
using HKShop.Application.Accounts;
using HKShop.Contracts.Ports;
using HKShop.Domain;
using HKShop.Web;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Hosting;

namespace HKShop.EOffice.Operations.Api;

[Route("api/eoffice/users")]
[Authorize(Policy = ShopPolicies.EOfficeAdmin)]
public sealed class EOfficeUsersController(
    IShopRequestContext request,
    IStaffDirectory staff,
    IEOfficeAuthService auth,
    ShopSettings settings,
    IWebHostEnvironment environment) : Controller
{
    [HttpPost("invite")]
    public async Task<IActionResult> Invite(string email, string displayName, string role)
    {
        var invitation = await staff.Invite(await request.Actor(), email, displayName, role);
        await SendInvitation(invitation);
        TempData["Notice"] = "Staff invitation created.";
        return Redirect("/eoffice/users");
    }

    [HttpPost("resend"), EnableRateLimiting("CustomerWrites")]
    public async Task<IActionResult> ResendInvitation(string id)
    {
        var invitation = await staff.GetPendingInvitation(await request.Actor(), id);
        await SendInvitation(invitation);
        TempData["Notice"] = "Invitation resent.";
        return Redirect("/eoffice/users");
    }

    [HttpPost("access")]
    public async Task<IActionResult> Access(string id, string role, bool disabled)
    {
        await staff.ChangeAccess(await request.Actor(), id, role, disabled);
        TempData["Notice"] = "Permissions updated.";
        return Redirect("/eoffice/users");
    }

    private async Task SendInvitation(StaffInvitation invitation)
    {
        var origin = settings.PublicOrigin.TrimEnd('/');
        if (origin.Length == 0 && environment.IsDevelopment()) origin = $"{Request.Scheme}://{Request.Host}";
        if (!Uri.TryCreate(origin, UriKind.Absolute, out var uri) || uri.Scheme != "https" || uri.AbsolutePath != "/" ||
            uri.Query.Length != 0 || uri.Fragment.Length != 0 || uri.UserInfo.Length != 0)
            throw new ShopRuleException("Configure the store's HTTPS address to send email.");
        var sent = await auth.SendStaffInvitationEmailAsync(invitation, origin, HttpContext.RequestAborted);
        if (!sent) TempData["Error"] = "The email could not be sent. You can request another one.";
    }
}
