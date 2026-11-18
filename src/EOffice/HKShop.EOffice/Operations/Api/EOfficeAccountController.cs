using HKShop.Application;
using HKShop.Application.Accounts;
using HKShop.Contracts.Ports;
using HKShop.Domain;
using HKShop.Web;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.AspNetCore.WebUtilities;

namespace HKShop.EOffice.Operations.Api;

[Route("api/eoffice/account")]
public sealed class EOfficeAccountController(IEOfficeAuthService auth) : Controller
{
    [HttpPost("login"), AllowAnonymous, EnableRateLimiting("CustomerWrites")]
    public async Task<IActionResult> Login(string email, string password, string? returnUrl)
    {
        var outcome = await auth.LoginAsync(email, password);
        var defaultTarget = ReturnPath(returnUrl, outcome.DefaultReturnPath);
        if (outcome.RequiresTwoFactor)
            return Redirect(QueryHelpers.AddQueryString("/eoffice/mfa", "returnUrl", defaultTarget));
        if (outcome.RequiresMfaEnrollment)
            return LocalRedirect(SecurityPath(defaultTarget));
        return LocalRedirect(defaultTarget);
    }

    [HttpPost("mfa"), AllowAnonymous, EnableRateLimiting("CustomerWrites")]
    public async Task<IActionResult> Mfa(string code, string? returnUrl)
    {
        var defaultPath = await auth.CompleteMfaAsync(code);
        if (defaultPath is null) return Redirect("/eoffice/login");
        return LocalRedirect(ReturnPath(returnUrl, defaultPath));
    }

    [HttpPost("security/prepare"), Authorize(Policy = ShopPolicies.EOfficeAccount), EnableRateLimiting("CustomerWrites")]
    public async Task<IActionResult> PrepareMfa(string password, string? returnUrl)
    {
        await auth.PrepareMfaAsync(User, password);
        return LocalRedirect(SecurityPath(returnUrl));
    }

    [HttpPost("security/enable"), Authorize(Policy = ShopPolicies.EOfficeAccount), EnableRateLimiting("CustomerWrites")]
    public async Task<IActionResult> EnableMfa(string code, string? returnUrl)
    {
        await auth.EnableMfaAsync(User, code);
        TempData["Notice"] = "Two-step verification is enabled. Please sign in again.";
        return LocalRedirect(ReturnLink("/eoffice/login", returnUrl));
    }

    [HttpPost("logout"), Authorize]
    public async Task<IActionResult> Logout()
    {
        await auth.LogoutAsync();
        return Redirect("/eoffice/login");
    }

    [HttpPost("profile"), Authorize(Policy = ShopPolicies.EOfficeAccount), RequestSizeLimit(UserProfileInput.MaxImageBytes + 65_536)]
    public async Task<IActionResult> Profile([FromForm] UserProfileInput input, IFormFile? profileImage, [FromServices] IUserProfiles profiles)
    {
        if (!ModelState.IsValid)
            throw new ShopRuleException(ModelState.Values.SelectMany(value => value.Errors).First().ErrorMessage);
        var user = await auth.RequireCurrentUserAsync(User);
        await using var image = profileImage?.OpenReadStream();
        await profiles.Update(user.Id, input, image, HttpContext.RequestAborted);
        TempData["Notice"] = "Profile saved.";
        return Redirect("/eoffice/profile");
    }

    private string ReturnPath(string? path, string fallback) => Url.IsLocalUrl(path) ? path! : fallback;
    private string ReturnLink(string path, string? returnUrl) => Url.IsLocalUrl(returnUrl)
        ? QueryHelpers.AddQueryString(path, "returnUrl", returnUrl!) : path;
    private string SecurityPath(string? returnUrl) => ReturnLink("/eoffice/security", returnUrl);
}
