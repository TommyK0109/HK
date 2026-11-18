using System.Security.Claims;
using HKShop.Application.Accounts;
using HKShop.Web;

namespace HKShop.Contracts.Ports;

public sealed record EOfficeLoginOutcome(string DefaultReturnPath, bool RequiresTwoFactor, bool RequiresMfaEnrollment);

public interface IEOfficeAuthService
{
    Task<EOfficeLoginOutcome> LoginAsync(string? email, string? password);
    Task<string?> CompleteMfaAsync(string? code);
    Task PrepareMfaAsync(ClaimsPrincipal principal, string? password);
    Task EnableMfaAsync(ClaimsPrincipal principal, string? code);
    Task LogoutAsync();
    Task<ShopUserIdentity> RequireCurrentUserAsync(ClaimsPrincipal principal);
    Task<string?> GetAuthenticatorKeyAsync(ClaimsPrincipal principal);
    Task<bool> SendStaffInvitationEmailAsync(StaffInvitation invitation, string baseOrigin, CancellationToken cancellationToken);
    Task<(string UserId, string Email)> LookupConfirmedUserForShipperAsync(string email);
    Task SyncShipperRoleAsync(string userId, bool active);
}
