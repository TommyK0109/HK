using System.Security.Claims;
using HKShop.Application.Accounts;
using HKShop.Web;

namespace HKShop.Contracts.Ports;

public sealed record CommerceLoginOutcome(string? UserId, bool RequiresTwoFactor);

public interface ICommerceAuthService
{
    Task<bool> RegisterAsync(RegisterInput input, string baseOrigin, CancellationToken cancellationToken);
    Task<bool> ConfirmEmailAsync(string userId, string token);
    Task<bool> ResendConfirmationAsync(string? email, string baseOrigin, CancellationToken cancellationToken);
    Task<CommerceLoginOutcome> LoginAsync(string? email, string? password);
    Task<string?> CompleteMfaAsync(string? code);
    Task PrepareMfaAsync(ClaimsPrincipal principal, string? password);
    Task EnableMfaAsync(ClaimsPrincipal principal, string? code);
    Task LogoutAsync();
    Task<bool> ForgotPasswordAsync(string? email, string baseOrigin, CancellationToken cancellationToken);
    Task ResetPasswordAsync(string? userId, string? token, string? password, string? confirmation);
    Task<ShopUserIdentity> RequireCurrentUserAsync(ClaimsPrincipal principal);
    Task<string?> GetAuthenticatorKeyAsync(ClaimsPrincipal principal);
}
