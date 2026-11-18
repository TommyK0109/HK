using HKShop.Application;
using HKShop.Application.Accounts;
using HKShop.Application.Checkout;
using HKShop.Application.Transportation;
using HKShop.Contracts.Ports;
using HKShop.Domain;
using HKShop.Web;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Hosting;

namespace HKShop.Commerce.Api;

[Route("api/commerce/account")]
public sealed class CommerceAccountController(
    ICommerceAuthService auth,
    ShopRequest request,
    CartService cart,
    AddressService addresses,
    ShopSettings settings,
    IWebHostEnvironment environment) : Controller
{
    [HttpPost("register"), AllowAnonymous, EnableRateLimiting("CustomerWrites")]
    public async Task<IActionResult> Register([FromForm] RegisterInput input)
    {
        if (!ModelState.IsValid)
            throw new ShopRuleException(ModelState.Values.SelectMany(v => v.Errors).First().ErrorMessage);
        var sent = await auth.RegisterAsync(input, ResolveOrigin(), HttpContext.RequestAborted);
        if (!sent) TempData["Error"] = "The email could not be sent. You can request another one.";
        else TempData["Notice"] = "Please check your inbox to confirm your account.";
        return Redirect("/account/login");
    }

    [HttpGet("/account/confirm"), AllowAnonymous]
    public async Task<IActionResult> Confirm(string userId, string token)
    {
        if (!await auth.ConfirmEmailAsync(userId, token))
            TempData["Error"] = "The confirmation link is invalid or has expired.";
        else
            TempData["Notice"] = "Your email has been confirmed. You can now sign in.";
        return Redirect("/account/login");
    }

    [HttpPost("resend"), AllowAnonymous, EnableRateLimiting("CustomerWrites")]
    public async Task<IActionResult> Resend(string email)
    {
        var sent = await auth.ResendConfirmationAsync(email, ResolveOrigin(), HttpContext.RequestAborted);
        if (!sent) TempData["Error"] = "The email could not be sent. You can request another one.";
        else TempData["Notice"] = "If the account needs confirmation, a link will be sent by email.";
        return Redirect("/account/login");
    }

    [HttpPost("login"), AllowAnonymous, EnableRateLimiting("CustomerWrites")]
    public async Task<IActionResult> Login(string email, string password, string? returnUrl)
    {
        var outcome = await auth.LoginAsync(email, password);
        if (outcome.RequiresTwoFactor)
            return Redirect(QueryHelpers.AddQueryString("/account/mfa", "returnUrl", ReturnPath(returnUrl, "/")));
        if (outcome.UserId is not null)
            await MergeCart(outcome.UserId);
        return LocalRedirect(ReturnPath(returnUrl, "/"));
    }

    [HttpPost("mfa"), AllowAnonymous, EnableRateLimiting("CustomerWrites")]
    public async Task<IActionResult> Mfa(string code, string? returnUrl)
    {
        var userId = await auth.CompleteMfaAsync(code);
        if (userId is null) return Redirect("/account/login");
        await MergeCart(userId);
        return LocalRedirect(ReturnPath(returnUrl, "/"));
    }

    [HttpPost("security/prepare"), Authorize(Policy = ShopPolicies.CommerceCustomer), EnableRateLimiting("CustomerWrites")]
    public async Task<IActionResult> PrepareMfa(string password, string? returnUrl)
    {
        await auth.PrepareMfaAsync(User, password);
        return LocalRedirect(SecurityPath(returnUrl));
    }

    [HttpPost("security/enable"), Authorize(Policy = ShopPolicies.CommerceCustomer), EnableRateLimiting("CustomerWrites")]
    public async Task<IActionResult> EnableMfa(string code, string? returnUrl)
    {
        await auth.EnableMfaAsync(User, code);
        TempData["Notice"] = "Two-step verification is enabled. Please sign in again.";
        return LocalRedirect(ReturnLink("/account/login", returnUrl));
    }

    [HttpPost("logout"), Authorize]
    public async Task<IActionResult> Logout()
    {
        await auth.LogoutAsync();
        return Redirect("/");
    }

    [HttpPost("forgot"), AllowAnonymous, EnableRateLimiting("CustomerWrites")]
    public async Task<IActionResult> Forgot(string email)
    {
        var sent = await auth.ForgotPasswordAsync(email, ResolveOrigin(), HttpContext.RequestAborted);
        if (!sent) TempData["Error"] = "The email could not be sent. You can request another one.";
        else TempData["Notice"] = "If the email is eligible, a reset link will be sent.";
        return Redirect("/account/login");
    }

    [HttpPost("reset"), AllowAnonymous, EnableRateLimiting("CustomerWrites")]
    public async Task<IActionResult> Reset(string userId, string token, string password, string? confirmation)
    {
        await auth.ResetPasswordAsync(userId, token, password, confirmation);
        TempData["Notice"] = "Your password has been set. You can now sign in.";
        return Redirect("/account/login");
    }

    [HttpPost("profile"), Authorize(Policy = ShopPolicies.CommerceCustomer), RequestSizeLimit(UserProfileInput.MaxImageBytes + 65_536)]
    public async Task<IActionResult> Profile([FromForm] UserProfileInput input, IFormFile? profileImage, [FromServices] IUserProfiles profiles)
    {
        if (!ModelState.IsValid)
            throw new ShopRuleException(ModelState.Values.SelectMany(value => value.Errors).First().ErrorMessage);
        var user = await auth.RequireCurrentUserAsync(User);
        await using var image = profileImage?.OpenReadStream();
        await profiles.Update(user.Id, input, image, HttpContext.RequestAborted);
        TempData["Notice"] = "Profile saved.";
        return Redirect("/account/profile");
    }

    [HttpGet("/account/profile/image"), Authorize]
    public async Task<IActionResult> ProfileImage([FromServices] IUserProfiles profiles)
    {
        var user = await auth.RequireCurrentUserAsync(User);
        var image = await profiles.GetImage(user.Id, HttpContext.RequestAborted);
        return image is null ? NotFound() : File(image.Data, image.ContentType);
    }

    [HttpPost("addresses/save"), Authorize(Policy = ShopPolicies.CommerceCustomer)]
    public async Task<IActionResult> SaveAddress(long id, string recipient, string phone, string address,
        string? latitude, string? longitude)
    {
        if (!ModelState.IsValid) throw new ShopRuleException("Invalid address or location coordinates.");
        var actor = await request.Actor();
        actor.RequireCustomer();
        var pin = string.IsNullOrWhiteSpace(latitude) && string.IsNullOrWhiteSpace(longitude)
            ? (TransportPoint?)null
            : TransportPoint.Parse(latitude, longitude, "the delivery address");
        await addresses.Save(actor.UserId, id, recipient, phone, address, pin?.Latitude, pin?.Longitude);
        TempData["Notice"] = "Address saved.";
        return Redirect("/account/addresses");
    }

    [HttpPost("addresses/delete"), Authorize(Policy = ShopPolicies.CommerceCustomer)]
    public async Task<IActionResult> DeleteAddress(long id)
    {
        var actor = await request.Actor();
        actor.RequireCustomer();
        await addresses.Delete(actor.UserId, id);
        TempData["Notice"] = "Address deleted.";
        return Redirect("/account/addresses");
    }

    private async Task MergeCart(string userId)
    {
        var guest = request.GuestHash();
        if (guest is null) return;
        var adjusted = await cart.Merge(userId, guest);
        request.ForgetGuest();
        if (adjusted > 0) TempData["Notice"] = "Your carts have been merged. Some quantities were adjusted based on stock.";
    }

    private string ResolveOrigin()
    {
        var origin = settings.PublicOrigin.TrimEnd('/');
        if (origin.Length == 0 && environment.IsDevelopment()) origin = $"{Request.Scheme}://{Request.Host}";
        return origin;
    }

    private string ReturnPath(string? path, string fallback) => Url.IsLocalUrl(path) ? path! : fallback;
    private string ReturnLink(string path, string? returnUrl) => Url.IsLocalUrl(returnUrl)
        ? QueryHelpers.AddQueryString(path, "returnUrl", returnUrl!) : path;
    private string SecurityPath(string? returnUrl) => ReturnLink("/account/security", returnUrl);
}
