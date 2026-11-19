using HKShop.Application;
using HKShop.Application.Sellers;
using HKShop.Domain;
using HKShop.Domain.Sellers;
using HKShop.Web;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Hosting;

namespace HKShop.Commerce.Api;

[Authorize]
[Route("api/commerce/seller")]
public sealed class SellerAccountController(
    SellerService sellers,
    ShopRequest request,
    ShopSettings settings,
    IWebHostEnvironment environment) : Controller
{
    [HttpPost("{sellerId:long}/invite"), Authorize(Policy = ShopPolicies.CommerceSeller), EnableRateLimiting("CustomerWrites")]
    public async Task<IActionResult> Invite([FromRoute] long sellerId, [FromForm] string email, [FromForm] SellerRole role)
    {
        if (!ModelState.IsValid || !Enum.IsDefined(role)) throw new ShopRuleException("The role is invalid.");
        var origin = settings.PublicOrigin;
        if (string.IsNullOrWhiteSpace(origin) && environment.IsDevelopment()) origin = $"{Request.Scheme}://{Request.Host}";
        await sellers.Invite(await request.Actor(), sellerId, email, role, origin);
        TempData["Notice"] = "The invitation email has been queued.";
        return Redirect($"/seller/{sellerId}");
    }

    [HttpPost("invitations/accept"), Authorize(Policy = ShopPolicies.CommerceSeller), EnableRateLimiting("CustomerWrites")]
    public async Task<IActionResult> Accept([FromForm] string token)
    {
        var sellerId = await sellers.Accept(await request.Actor(), token);
        TempData["Notice"] = "You have joined the store.";
        return Redirect($"/seller/{sellerId}");
    }

    [HttpPost("{sellerId:long}/members/revoke"), Authorize(Policy = ShopPolicies.CommerceSeller)]
    public async Task<IActionResult> RevokeMember([FromRoute] long sellerId, [FromForm] string userId)
    {
        await sellers.RevokeMember(await request.Actor(), sellerId, userId);
        TempData["Notice"] = "Member access revoked.";
        return Redirect($"/seller/{sellerId}");
    }

    [HttpPost("{sellerId:long}/invitations/{invitationId:long}/revoke"), Authorize(Policy = ShopPolicies.CommerceSeller)]
    public async Task<IActionResult> RevokeInvitation([FromRoute] long sellerId, [FromRoute] long invitationId)
    {
        await sellers.RevokeInvitation(await request.Actor(), sellerId, invitationId);
        TempData["Notice"] = "Invitation revoked.";
        return Redirect($"/seller/{sellerId}");
    }
}
