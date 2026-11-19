using System.ComponentModel.DataAnnotations;
using System.Security.Cryptography;
using System.Text;
using HKShop.Application;
using HKShop.Application.Transportation;
using HKShop.Contracts.Clients;
using HKShop.Contracts.Events;
using HKShop.Domain;
using HKShop.Identity.Api.Domain;
using HKShop.Identity.Api.Infrastructure;
using HKShop.Messaging;
using HKShop.ServiceDefaults;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;

namespace HKShop.Identity.Api.Application;

public sealed class JwtTokenIssuer(
    UserManager<ShopUser> users,
    IdentityServiceDbContext db,
    JwtSettings jwtSettings,
    ShopSettings shopSettings,
    TimeProvider time)
{
    public async Task<TokenPairResponse> IssueTokensAsync(IssueTokenRequest request, CancellationToken ct)
    {
        var email = request.Email?.Trim();
        var user = !string.IsNullOrEmpty(email) && new EmailAddressAttribute().IsValid(email)
            ? await users.FindByEmailAsync(email)
            : null;

        if (user is null || user.Disabled || !user.EmailConfirmed ||
            string.IsNullOrEmpty(request.Password) || request.Password.Length > 128)
            throw new ShopRuleException("Sign-in failed. Check your credentials and confirm your email.");

        if (await users.IsLockedOutAsync(user))
            throw new ShopRuleException("Sign-in failed. Check your credentials and confirm your email.");

        if (!await users.CheckPasswordAsync(user, request.Password))
        {
            await users.AccessFailedAsync(user);
            throw new ShopRuleException("Sign-in failed. Check your credentials and confirm your email.");
        }
        await users.ResetAccessFailedCountAsync(user);

        var roles = await users.GetRolesAsync(user);
        var isStaff = roles.Contains("StoreStaff");
        var isAdmin = roles.Contains("Admin");
        var isShipper = roles.Contains("Shipper");
        var hasEmployeeRole = isStaff || isAdmin || isShipper;

        var isEOffice = string.Equals(request.Platform, "eoffice", StringComparison.OrdinalIgnoreCase);
        if (!isEOffice && hasEmployeeRole)
            throw new ShopRuleException("Employee and shipper accounts must sign in through EOffice (/eoffice/login).");
        if (isEOffice && !hasEmployeeRole)
            throw new ShopRuleException("Only HKShop staff, administrators, and shippers can sign in to EOffice.");

        var defaultPath = isEOffice
            ? (isShipper && !isStaff && !isAdmin ? "/eoffice/shipper" : "/eoffice")
            : "/";

        var mfaVerified = false;
        if (user.TwoFactorEnabled)
        {
            if (string.IsNullOrWhiteSpace(request.TwoFactorCode))
                return new TokenPairResponse(true, false, null, null, null, defaultPath);

            var normalized = request.TwoFactorCode.Replace(" ", "").Replace("-", "");
            if (!await users.VerifyTwoFactorTokenAsync(user, TokenOptions.DefaultAuthenticatorProvider, normalized))
                throw new ShopRuleException("The verification code is invalid.");
            mfaVerified = true;
        }
        else if (isEOffice)
        {
            return new TokenPairResponse(false, true, null, null, null, defaultPath);
        }

        return await MintTokenPairAsync(user, roles, mfaVerified, defaultPath, ct);
    }

    public async Task<TokenPairResponse> RefreshAsync(string refreshToken, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(refreshToken))
            throw new ShopRuleException("Invalid refresh token.");

        var hash = Hash(refreshToken);
        var now = time.GetUtcNow();
        var stored = await db.RefreshTokens.FirstOrDefaultAsync(t => t.TokenHash == hash, ct)
            ?? throw new ShopRuleException("Invalid refresh token.");

        if (stored.RevokedAt is not null || stored.ExpiresAt <= now)
            throw new ShopRuleException("The session has expired. Please sign in again.");

        var user = await users.FindByIdAsync(stored.UserId);
        if (user is null || user.Disabled || !string.Equals(user.SecurityStamp, stored.SecurityStamp, StringComparison.Ordinal))
        {
            stored.RevokedAt = now;
            await db.SaveChangesAsync(ct);
            throw new ShopRuleException("The session is no longer valid. Please sign in again.");
        }

        var roles = await users.GetRolesAsync(user);
        var rawNext = GenerateRawToken();
        stored.RevokedAt = now;
        stored.ReplacedByTokenHash = Hash(rawNext);

        var pair = await MintTokenPairAsync(user, roles, stored.MfaVerified, null, ct, rawNext);
        return pair;
    }

    public async Task RevokeAsync(string refreshToken, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(refreshToken)) return;
        var hash = Hash(refreshToken);
        var stored = await db.RefreshTokens.FirstOrDefaultAsync(t => t.TokenHash == hash, ct);
        if (stored is not null && stored.RevokedAt is null)
        {
            stored.RevokedAt = time.GetUtcNow();
            await db.SaveChangesAsync(ct);
        }
    }

    public async Task RegisterAsync(string email, string displayName, string password, CancellationToken ct)
    {
        email = ShopRules.Text(email, 256, "Email");
        displayName = ShopRules.Text(displayName, 100, "Display name");
        if (string.IsNullOrEmpty(password) || password.Length is < 10 or > 128)
            throw new ShopRuleException("The password must contain at least 10 characters.");

        var user = new ShopUser { Email = email, UserName = email, DisplayName = displayName };
        var created = await users.CreateAsync(user, password);
        if (!created.Succeeded)
            throw new ShopRuleException(created.Errors.First().Code is "DuplicateEmail" or "DuplicateUserName"
                ? "This email is already in use."
                : "Could not create your account.");

        var token = await users.GenerateEmailConfirmationTokenAsync(user);
        var origin = string.IsNullOrWhiteSpace(shopSettings.PublicOrigin) ? "https://localhost" : shopSettings.PublicOrigin.TrimEnd('/');
        var link = QueryHelpers.AddQueryString(origin + "/account/confirm",
            new Dictionary<string, string?> { ["userId"] = user.Id, ["token"] = token });
        var now = time.GetUtcNow();
        db.EnqueueOutboxEvent("User", user.Id, nameof(UserRegistered),
            new UserRegistered(user.Id, user.Email!, user.DisplayName, link, now),
            $"UserRegistered:{user.Id}", now);
        await db.SaveChangesAsync(ct);
    }

    public async Task SetUserDisabledAsync(ShopActor actor, string userId, bool disabled, CancellationToken ct)
    {
        actor.RequireAdmin();
        var user = await users.FindByIdAsync(userId) ?? throw new ShopRuleException("User not found.");
        user.Disabled = disabled;
        await users.UpdateAsync(user);
        await users.UpdateSecurityStampAsync(user);

        var now = time.GetUtcNow();
        var activeTokens = await db.RefreshTokens.Where(t => t.UserId == userId && t.RevokedAt == null).ToListAsync(ct);
        foreach (var token in activeTokens) token.RevokedAt = now;

        db.AddAuditEntry(actor.UserId, disabled ? "User disabled" : "User enabled", userId, now);
        if (disabled)
        {
            db.EnqueueOutboxEvent("User", user.Id, nameof(UserDisabled),
                new UserDisabled(user.Id, user.Email ?? "", now),
                $"UserDisabled:{user.Id}:{now.ToUnixTimeMilliseconds()}", now);
        }
        await db.SaveChangesAsync(ct);
    }

    public async Task SetShipperRoleAsync(ShopActor actor, string email, bool active, CancellationToken ct)
    {
        actor.RequireAdmin();
        var user = await users.FindByEmailAsync(email.Trim())
            ?? throw new ShopRuleException("Use a registered account with a verified email.");
        if (!user.EmailConfirmed)
            throw new ShopRuleException("Use a registered account with a verified email.");

        var inRole = await users.IsInRoleAsync(user, "Shipper");
        if (active && !inRole) await users.AddToRoleAsync(user, "Shipper");
        else if (!active && inRole) await users.RemoveFromRoleAsync(user, "Shipper");

        await users.UpdateSecurityStampAsync(user);
        var roles = await users.GetRolesAsync(user);
        var now = time.GetUtcNow();
        db.AddAuditEntry(actor.UserId, active ? "Shipper enabled" : "Shipper disabled", user.Id, now);
        db.EnqueueOutboxEvent("User", user.Id, nameof(UserRolesChanged),
            new UserRolesChanged(user.Id, user.Email ?? "", roles.ToList(), active, now),
            $"UserRolesChanged:{user.Id}:{now.ToUnixTimeMilliseconds()}", now);
        await db.SaveChangesAsync(ct);
    }

    public async Task<IReadOnlyList<CustomerAddressDto>> ListAddressesAsync(string customerId, CancellationToken ct) =>
        await db.CustomerAddresses.AsNoTracking()
            .Where(a => a.CustomerId == customerId)
            .OrderBy(a => a.Id)
            .Select(a => new CustomerAddressDto(a.Id, a.CustomerId, a.Recipient, a.Phone, a.AddressText, a.Latitude, a.Longitude))
            .ToListAsync(ct);

    public async Task<CustomerAddressDto?> GetAddressAsync(string customerId, long addressId, CancellationToken ct) =>
        await db.CustomerAddresses.AsNoTracking()
            .Where(a => a.Id == addressId && a.CustomerId == customerId)
            .Select(a => new CustomerAddressDto(a.Id, a.CustomerId, a.Recipient, a.Phone, a.AddressText, a.Latitude, a.Longitude))
            .FirstOrDefaultAsync(ct);

    public async Task<long> SaveAddressAsync(
        string customerId, long id, string recipient, string phone, string address, double? lat, double? lng, CancellationToken ct)
    {
        recipient = ShopRules.Text(recipient, 100, "Recipient");
        phone = ShopRules.Text(phone, 20, "Phone number");
        address = ShopRules.Text(address, 300, "Address");
        if (lat.HasValue || lng.HasValue)
            TransportPoint.Require(lat, lng, "the delivery address");

        var row = id == 0
            ? new CustomerAddress { CustomerId = customerId }
            : await db.CustomerAddresses.FirstOrDefaultAsync(a => a.Id == id && a.CustomerId == customerId, ct)
              ?? throw new ShopRuleException("The shipping address does not exist.");
        row.Recipient = recipient;
        row.Phone = phone;
        row.AddressText = address;
        row.Latitude = lat;
        row.Longitude = lng;
        if (id == 0) db.CustomerAddresses.Add(row);
        await db.SaveChangesAsync(ct);
        return row.Id;
    }

    public async Task DeleteAddressAsync(string customerId, long addressId, CancellationToken ct)
    {
        var row = await db.CustomerAddresses.FirstOrDefaultAsync(a => a.Id == addressId && a.CustomerId == customerId, ct)
            ?? throw new ShopRuleException("The shipping address does not exist.");
        db.CustomerAddresses.Remove(row);
        await db.SaveChangesAsync(ct);
    }

    private async Task<TokenPairResponse> MintTokenPairAsync(
        ShopUser user, IList<string> roles, bool mfaVerified, string? defaultPath, CancellationToken ct, string? rawRefresh = null)
    {
        var isStaff = roles.Contains("StoreStaff");
        var isAdmin = roles.Contains("Admin");
        var isShipper = roles.Contains("Shipper");
        var actor = new ShopActor(
            user.Id,
            user.Email ?? "",
            user.EmailConfirmed,
            Staff: mfaVerified && isStaff,
            Admin: mfaVerified && isAdmin,
            Shipper: mfaVerified && isShipper)
        {
            Mfa = mfaVerified,
            HasEmployeeRole = isStaff || isAdmin || isShipper
        };

        var now = time.GetUtcNow();
        var lifetime = TimeSpan.FromMinutes(Math.Max(1, jwtSettings.AccessTokenMinutes));
        var accessToken = ShopActorFactory.CreateJwtToken(actor, jwtSettings, now, lifetime);

        rawRefresh ??= GenerateRawToken();
        db.RefreshTokens.Add(new RefreshToken
        {
            UserId = user.Id,
            TokenHash = Hash(rawRefresh),
            SecurityStamp = user.SecurityStamp ?? "",
            MfaVerified = mfaVerified,
            CreatedAt = now,
            ExpiresAt = now.AddDays(30)
        });
        await db.SaveChangesAsync(ct);

        return new TokenPairResponse(false, false, accessToken, rawRefresh, now.Add(lifetime), defaultPath);
    }

    private static string GenerateRawToken() => Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
    private static string Hash(string token) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));
}
