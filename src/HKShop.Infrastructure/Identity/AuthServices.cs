using System.ComponentModel.DataAnnotations;
using System.Data;
using System.Security.Claims;
using HKShop.Application;
using HKShop.Application.Accounts;
using HKShop.Contracts.Ports;
using HKShop.Domain;
using HKShop.Domain.Orders;
using HKShop.Infrastructure.Email;
using HKShop.Infrastructure.Persistence;
using HKShop.Web;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace HKShop.Infrastructure.Identity;

public sealed class ShopUserResolver(UserManager<ShopUser> users, ShopDbContext db) : IShopUserResolver
{
    public string? GetUserId(ClaimsPrincipal principal) =>
        principal.Identity?.IsAuthenticated == true ? users.GetUserId(principal) : null;

    public async Task<ShopUserIdentity?> GetIdentityAsync(ClaimsPrincipal principal)
    {
        if (principal.Identity?.IsAuthenticated != true) return null;
        var user = await users.GetUserAsync(principal);
        return user is null
            ? null
            : new ShopUserIdentity(user.Id, user.Email, user.DisplayName, user.ContactPhone,
                user.EmailConfirmed, user.TwoFactorEnabled, user.Disabled);
    }

    public async Task<ShopActor> LoadActorAsync(ClaimsPrincipal principal)
    {
        if (principal.Identity?.IsAuthenticated != true)
            return new("", "", false, false, false, false);
        var user = await users.GetUserAsync(principal);
        if (user is null || user.Disabled)
            return new("", "", false, false, false, false);
        var roles = await users.GetRolesAsync(user);
        var isStaffRole = roles.Contains("StoreStaff");
        var isAdminRole = roles.Contains("Admin");
        var isShipperRole = roles.Contains("Shipper");
        var hasTransportShipper = await db.Set<TransportShipper>().AsNoTracking()
            .AnyAsync(s => s.UserId == user.Id && s.Active);
        var hasEmployeeRole = isStaffRole || isAdminRole || isShipperRole || hasTransportShipper;
        var mfa = user.EmailConfirmed && user.TwoFactorEnabled &&
            (principal.HasClaim("amr", "mfa") || principal.HasClaim(ClaimTypes.AuthenticationMethod, "mfa"));
        return new(user.Id, user.Email ?? "", user.EmailConfirmed,
            mfa && isStaffRole, mfa && isAdminRole, mfa && (isShipperRole || hasTransportShipper))
        {
            Mfa = mfa,
            HasEmployeeRole = hasEmployeeRole
        };
    }
}

public sealed class CommerceAuthService(
    UserManager<ShopUser> users,
    SignInManager<ShopUser> signIn,
    ShopDbContext db,
    SmtpSender smtp,
    ILogger<CommerceAuthService> logger) : ICommerceAuthService
{
    public async Task<bool> RegisterAsync(RegisterInput input, string baseOrigin, CancellationToken cancellationToken)
    {
        var email = input.Email.Trim();
        var user = new ShopUser { Email = email, UserName = email, DisplayName = input.DisplayName.Trim() };
        try { Require(await users.CreateAsync(user, input.Password)); }
        catch (DbUpdateException error) when (error.InnerException is SqlException { Number: 2601 or 2627 })
        { throw new ShopRuleException("This email is already in use."); }
        return await SendConfirmation(user, baseOrigin, cancellationToken);
    }

    public async Task<bool> ConfirmEmailAsync(string userId, string token)
    {
        var user = await users.FindByIdAsync(userId);
        return user is not null && !user.Disabled && (await users.ConfirmEmailAsync(user, token)).Succeeded;
    }

    public async Task<bool> ResendConfirmationAsync(string? email, string baseOrigin, CancellationToken cancellationToken)
    {
        var user = await FindByEmail(email);
        if (user is { EmailConfirmed: false, Disabled: false })
            return await SendConfirmation(user, baseOrigin, cancellationToken);
        return true;
    }

    public async Task<CommerceLoginOutcome> LoginAsync(string? email, string? password)
    {
        var user = await FindByEmail(email);
        if (user is null || user.Disabled || string.IsNullOrEmpty(password) || password.Length > 128)
            throw new ShopRuleException("Sign-in failed. Check your credentials and confirm your email.");
        if (await HasEmployeeRole(user))
            throw new ShopRuleException("Employee and shipper accounts must sign in through EOffice (/eoffice/login).");
        var result = await signIn.PasswordSignInAsync(user, password, isPersistent: false, lockoutOnFailure: true);
        if (result.RequiresTwoFactor)
            return new CommerceLoginOutcome(null, true);
        if (!result.Succeeded)
            throw new ShopRuleException("Sign-in failed. Check your credentials and confirm your email.");
        return new CommerceLoginOutcome(user.Id, false);
    }

    public async Task<string?> CompleteMfaAsync(string? code)
    {
        var user = await signIn.GetTwoFactorAuthenticationUserAsync();
        if (user is null || user.Disabled) return null;
        if (await HasEmployeeRole(user))
            throw new ShopRuleException("Employee and shipper accounts must sign in through EOffice (/eoffice/login).");
        var result = await signIn.TwoFactorAuthenticatorSignInAsync(NormalizeCode(code), isPersistent: false, rememberClient: false);
        if (!result.Succeeded) throw new ShopRuleException("The verification code is invalid.");
        return user.Id;
    }

    public async Task PrepareMfaAsync(ClaimsPrincipal principal, string? password)
    {
        var user = await Current(principal);
        if (user.TwoFactorEnabled) return;
        if (string.IsNullOrEmpty(password) || password.Length > 128 ||
            !(await signIn.CheckPasswordSignInAsync(user, password, lockoutOnFailure: true)).Succeeded)
            throw new ShopRuleException("The password is incorrect.");
        if (await users.GetAuthenticatorKeyAsync(user) is null)
        {
            Require(await users.ResetAuthenticatorKeyAsync(user));
            await signIn.RefreshSignInAsync(user);
        }
    }

    public async Task EnableMfaAsync(ClaimsPrincipal principal, string? code)
    {
        var user = await Current(principal);
        if (user.TwoFactorEnabled) return;
        if (!await users.VerifyTwoFactorTokenAsync(user, TokenOptions.DefaultAuthenticatorProvider, NormalizeCode(code)))
            throw new ShopRuleException("The verification code is invalid.");
        Require(await users.SetTwoFactorEnabledAsync(user, true));
        await signIn.SignOutAsync();
    }

    public Task LogoutAsync() => signIn.SignOutAsync();

    public async Task<bool> ForgotPasswordAsync(string? email, string baseOrigin, CancellationToken cancellationToken)
    {
        var user = await FindByEmail(email);
        if (user is { EmailConfirmed: true, Disabled: false })
        {
            var token = await users.GeneratePasswordResetTokenAsync(user);
            var link = QueryHelpers.AddQueryString(baseOrigin + "/account/reset",
                new Dictionary<string, string?> { ["userId"] = user.Id, ["token"] = token });
            return await Send(user.Id, user.Email!, "Reset your HKShop password", link, cancellationToken);
        }
        return true;
    }

    public async Task ResetPasswordAsync(string? userId, string? token, string? password, string? confirmation)
    {
        var user = userId is null ? null : await users.FindByIdAsync(userId);
        if (user is null || user.Disabled || string.IsNullOrEmpty(token) || string.IsNullOrEmpty(password) || password.Length is < 10 or > 128)
            throw new ShopRuleException("The link is invalid or the password does not meet the requirements.");
        if (!string.IsNullOrEmpty(confirmation) && !user.EmailConfirmed)
            Require(await users.ConfirmEmailAsync(user, confirmation));
        Require(await users.ResetPasswordAsync(user, token, password));
    }

    public async Task<ShopUserIdentity> RequireCurrentUserAsync(ClaimsPrincipal principal)
    {
        var user = await Current(principal);
        return new ShopUserIdentity(user.Id, user.Email, user.DisplayName, user.ContactPhone,
            user.EmailConfirmed, user.TwoFactorEnabled, user.Disabled);
    }

    public async Task<string?> GetAuthenticatorKeyAsync(ClaimsPrincipal principal)
    {
        var user = await users.GetUserAsync(principal);
        return user is null ? null : await users.GetAuthenticatorKeyAsync(user);
    }

    private async Task<ShopUser> Current(ClaimsPrincipal principal)
    {
        var user = await users.GetUserAsync(principal);
        if (user is null || user.Disabled) throw new ShopRuleException("Please sign in again.");
        return user;
    }

    private async Task<ShopUser?> FindByEmail(string? email)
    {
        email = email?.Trim();
        return email is { Length: > 0 and <= 256 } && new EmailAddressAttribute().IsValid(email)
            ? await users.FindByEmailAsync(email) : null;
    }

    private async Task<bool> HasEmployeeRole(ShopUser user)
    {
        var roles = await users.GetRolesAsync(user);
        if (roles.Contains("Admin") || roles.Contains("StoreStaff") || roles.Contains("Shipper"))
            return true;
        return await db.Set<TransportShipper>().AsNoTracking().AnyAsync(s => s.UserId == user.Id && s.Active);
    }

    private async Task<bool> SendConfirmation(ShopUser user, string baseOrigin, CancellationToken cancellationToken)
    {
        var token = await users.GenerateEmailConfirmationTokenAsync(user);
        var link = QueryHelpers.AddQueryString(baseOrigin + "/account/confirm",
            new Dictionary<string, string?> { ["userId"] = user.Id, ["token"] = token });
        return await Send(user.Id, user.Email!, "Confirm your HKShop email", link, cancellationToken);
    }

    private async Task<bool> Send(string userId, string email, string subject, string link, CancellationToken cancellationToken)
    {
        try
        {
            await smtp.Send(email, subject, link, cancellationToken);
            return true;
        }
        catch (Exception error) when (!cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning("Account email failed for {UserId} ({ErrorType}).", userId, error.GetType().Name);
            return false;
        }
    }

    private static string NormalizeCode(string? code) => (code ?? "").Replace(" ", "").Replace("-", "");

    private static void Require(IdentityResult result)
    {
        if (result.Succeeded) return;
        var error = result.Errors.First().Code;
        throw new ShopRuleException(error switch
        {
            "DuplicateEmail" or "DuplicateUserName" => "This email is already in use.",
            "InvalidEmail" or "InvalidUserName" => "The email address is invalid.",
            "InvalidToken" => "The link is invalid or has expired.",
            "PasswordTooShort" => "The password must contain at least 10 characters.",
            _ => "Could not update your account. Check your details and try again."
        });
    }
}

public sealed class EOfficeAuthService(
    UserManager<ShopUser> users,
    SignInManager<ShopUser> signIn,
    ShopDbContext db,
    IShippingAdminPort shipping,
    SmtpSender smtp,
    ILogger<EOfficeAuthService> logger) : IEOfficeAuthService
{
    public async Task<EOfficeLoginOutcome> LoginAsync(string? email, string? password)
    {
        var user = await FindByEmail(email);
        if (user is null || user.Disabled || string.IsNullOrEmpty(password) || password.Length > 128)
            throw new ShopRuleException("Sign-in failed. Check your credentials and confirm your email.");
        var (isStaffOrAdmin, isShipper) = await GetEOfficeRoles(user);
        if (!isStaffOrAdmin && !isShipper)
            throw new ShopRuleException("Only HKShop staff, administrators, and shippers can sign in to EOffice.");
        var defaultPath = isShipper && !isStaffOrAdmin ? "/eoffice/shipper" : "/eoffice";
        var result = await signIn.PasswordSignInAsync(user, password, isPersistent: false, lockoutOnFailure: true);
        if (result.RequiresTwoFactor)
            return new EOfficeLoginOutcome(defaultPath, true, false);
        if (!result.Succeeded)
            throw new ShopRuleException("Sign-in failed. Check your credentials and confirm your email.");
        return new EOfficeLoginOutcome(defaultPath, false, !user.TwoFactorEnabled);
    }

    public async Task<string?> CompleteMfaAsync(string? code)
    {
        var user = await signIn.GetTwoFactorAuthenticationUserAsync();
        if (user is null || user.Disabled) return null;
        var (isStaffOrAdmin, isShipper) = await GetEOfficeRoles(user);
        if (!isStaffOrAdmin && !isShipper)
            throw new ShopRuleException("Only HKShop staff, administrators, and shippers can sign in to EOffice.");
        var result = await signIn.TwoFactorAuthenticatorSignInAsync(NormalizeCode(code), isPersistent: false, rememberClient: false);
        if (!result.Succeeded) throw new ShopRuleException("The verification code is invalid.");
        return isShipper && !isStaffOrAdmin ? "/eoffice/shipper" : "/eoffice";
    }

    public async Task PrepareMfaAsync(ClaimsPrincipal principal, string? password)
    {
        var user = await Current(principal);
        if (user.TwoFactorEnabled) return;
        if (string.IsNullOrEmpty(password) || password.Length > 128 ||
            !(await signIn.CheckPasswordSignInAsync(user, password, lockoutOnFailure: true)).Succeeded)
            throw new ShopRuleException("The password is incorrect.");
        if (await users.GetAuthenticatorKeyAsync(user) is null)
        {
            Require(await users.ResetAuthenticatorKeyAsync(user));
            await signIn.RefreshSignInAsync(user);
        }
    }

    public async Task EnableMfaAsync(ClaimsPrincipal principal, string? code)
    {
        var user = await Current(principal);
        if (user.TwoFactorEnabled) return;
        if (!await users.VerifyTwoFactorTokenAsync(user, TokenOptions.DefaultAuthenticatorProvider, NormalizeCode(code)))
            throw new ShopRuleException("The verification code is invalid.");
        Require(await users.SetTwoFactorEnabledAsync(user, true));
        await signIn.SignOutAsync();
    }

    public Task LogoutAsync() => signIn.SignOutAsync();

    public async Task<ShopUserIdentity> RequireCurrentUserAsync(ClaimsPrincipal principal)
    {
        var user = await Current(principal);
        return new ShopUserIdentity(user.Id, user.Email, user.DisplayName, user.ContactPhone,
            user.EmailConfirmed, user.TwoFactorEnabled, user.Disabled);
    }

    public async Task<string?> GetAuthenticatorKeyAsync(ClaimsPrincipal principal)
    {
        var user = await users.GetUserAsync(principal);
        return user is null ? null : await users.GetAuthenticatorKeyAsync(user);
    }

    public async Task<bool> SendStaffInvitationEmailAsync(StaffInvitation invitation, string baseOrigin, CancellationToken cancellationToken)
    {
        var link = QueryHelpers.AddQueryString(baseOrigin + "/account/reset", new Dictionary<string, string?>
        {
            ["userId"] = invitation.UserId,
            ["token"] = invitation.ResetToken,
            ["confirmation"] = invitation.ConfirmationToken
        });
        try
        {
            await smtp.Send(invitation.Email, "HKShop staff invitation", link, cancellationToken);
            return true;
        }
        catch (Exception error) when (!cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning("Staff invitation email failed for {UserId} ({ErrorType}).", invitation.UserId, error.GetType().Name);
            return false;
        }
    }

    public async Task SetShipperAccessAsync(ShopActor actor, string email, bool active)
    {
        actor.RequireAdmin();
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable);
        var user = await users.FindByEmailAsync(email);
        if (user is null || !user.EmailConfirmed)
            throw new ShopRuleException("Use a registered account with a verified email.");
        await shipping.ConfigureShipperAsync(new SqlShopStore.Session(db), actor, user.Id, user.Email!, active);
        var inRole = await users.IsInRoleAsync(user, "Shipper");
        if (active && !inRole)
            Require(await users.AddToRoleAsync(user, "Shipper"));
        else if (!active && inRole)
            Require(await users.RemoveFromRoleAsync(user, "Shipper"));
        await db.SaveChangesAsync();
        await transaction.CommitAsync();
    }

    private async Task<ShopUser> Current(ClaimsPrincipal principal)
    {
        var user = await users.GetUserAsync(principal);
        if (user is null || user.Disabled) throw new ShopRuleException("Please sign in again.");
        return user;
    }

    private async Task<ShopUser?> FindByEmail(string? email)
    {
        email = email?.Trim();
        return email is { Length: > 0 and <= 256 } && new EmailAddressAttribute().IsValid(email)
            ? await users.FindByEmailAsync(email) : null;
    }

    private async Task<(bool IsStaffOrAdmin, bool IsShipper)> GetEOfficeRoles(ShopUser user)
    {
        var roles = await users.GetRolesAsync(user);
        var isStaffOrAdmin = roles.Contains("Admin") || roles.Contains("StoreStaff");
        var isShipper = roles.Contains("Shipper") ||
            await db.Set<TransportShipper>().AsNoTracking().AnyAsync(s => s.UserId == user.Id && s.Active);
        return (isStaffOrAdmin, isShipper);
    }

    private static string NormalizeCode(string? code) => (code ?? "").Replace(" ", "").Replace("-", "");

    private static void Require(IdentityResult result)
    {
        if (result.Succeeded) return;
        var error = result.Errors.First().Code;
        throw new ShopRuleException(error switch
        {
            "DuplicateEmail" or "DuplicateUserName" => "This email is already in use.",
            "InvalidEmail" or "InvalidUserName" => "The email address is invalid.",
            "InvalidToken" => "The link is invalid or has expired.",
            "PasswordTooShort" => "The password must contain at least 10 characters.",
            _ => "Could not update your account. Check your details and try again."
        });
    }
}
