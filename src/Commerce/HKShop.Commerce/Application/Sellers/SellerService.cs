using System.ComponentModel.DataAnnotations;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using HKShop.Domain;
using HKShop.Domain.Orders;
using HKShop.Domain.Sellers;

namespace HKShop.Application.Sellers;

public enum SellerPermission { Workspace, ManageTeam, Catalog, Fulfillment, Finance, Audit }

public sealed record SellerWorkspace(long Id, string Slug, string DisplayName, SellerStatus Status, SellerRole Role)
{
    public bool Allows(SellerPermission permission) => permission switch
    {
        SellerPermission.Workspace => true,
        SellerPermission.ManageTeam or SellerPermission.Audit => Status == SellerStatus.Active && Role == SellerRole.Owner,
        SellerPermission.Catalog => Status == SellerStatus.Active && Role is SellerRole.Owner or SellerRole.CatalogManager,
        SellerPermission.Fulfillment => Status is SellerStatus.Active or SellerStatus.Closed && Role is SellerRole.Owner or SellerRole.Fulfillment,
        SellerPermission.Finance => Status is SellerStatus.Active or SellerStatus.Closed && Role is SellerRole.Owner or SellerRole.Finance,
        _ => false
    };
}

public sealed class SellerAccessDeniedException() : Exception("You don't have access to this seller.");

public static class SellerAccess
{
    public static async Task<SellerWorkspace> Require(IShopSession db, ShopActor actor, long sellerId, SellerPermission permission)
    {
        RequireMfa(actor);
        var workspace = await db.First(Workspaces(db, actor.UserId, sellerId));
        if (workspace is null || !workspace.Allows(permission)) throw new SellerAccessDeniedException();
        return workspace;
    }

    internal static void RequireMfa(ShopActor actor)
    {
        if (string.IsNullOrEmpty(actor.UserId) || !actor.Verified || !actor.Mfa) throw new SellerAccessDeniedException();
    }

    internal static IQueryable<SellerWorkspace> Workspaces(IShopSession db, string userId, long? sellerId = null) =>
        from membership in db.Query<SellerMembership>()
        join seller in db.Query<Seller>() on membership.SellerId equals seller.Id
        where membership.UserId == userId && membership.Status == SellerMembershipStatus.Active &&
            seller.Status != SellerStatus.Pending && (sellerId == null || seller.Id == sellerId)
        orderby seller.DisplayName, seller.Id
        select new SellerWorkspace(seller.Id, seller.Slug, seller.DisplayName, seller.Status, membership.Role);
}

public sealed record SellerTeamMember(string UserId, string Email, SellerRole Role, SellerMembershipStatus Status);
public sealed record SellerInvitationInfo(long Id, string Email, SellerRole Role, DateTimeOffset ExpiresAt);
public sealed record SellerTeam(IReadOnlyList<SellerTeamMember> Memberships, IReadOnlyList<SellerInvitationInfo> Invitations);

public sealed class SellerService(IShopStore store, TimeProvider time)
{
    public Task<List<SellerWorkspace>> Mine(ShopActor actor)
    {
        SellerAccess.RequireMfa(actor);
        return store.Read(db => db.List(SellerAccess.Workspaces(db, actor.UserId)));
    }

    public Task<Page<PlatformSeller>> PlatformList(ShopActor actor, int page)
    {
        actor.RequireAdmin();
        return store.Read(async db =>
        {
            const int size = 25;
            var sellers = db.Query<Seller>().Where(s => s.Status != SellerStatus.Pending);
            var owners = db.Query<SellerMembership>().Where(m => m.Role == SellerRole.Owner);
            var count = await db.Count(sellers);
            var number = Math.Clamp(page, 1, Math.Max(1, (int)Math.Ceiling(count / (double)size)));
            var items = await db.List(sellers.OrderBy(s => s.Status).ThenByDescending(s => s.Id).Skip((number - 1) * size).Take(size)
                .Select(s => new PlatformSeller(s.Id, s.Slug, s.DisplayName, s.Status, s.CreatedAt,
                    owners.Where(m => m.SellerId == s.Id).Select(m => m.Email).FirstOrDefault())));
            return new Page<PlatformSeller>(items, number, count, size);
        });
    }

    public Task<bool> SetStatus(ShopActor actor, long sellerId, SellerStatus status, string? reason)
    {
        actor.RequireAdmin();
        reason = ShopRules.Text(reason, 250, "Reason", required: status == SellerStatus.Suspended);
        return store.Write(async db =>
        {
            var seller = await db.First(db.Query<Seller>().Where(s => s.Id == sellerId))
                ?? throw new ShopRuleException("The store was not found.");
            if (seller.Status == status) return false;
            var action = (seller.Status, status) switch
            {
                (SellerStatus.Suspended, SellerStatus.Active) => "Khôi phục gian hàng",
                (SellerStatus.Active, SellerStatus.Suspended) when sellerId != Seller.FirstPartyId => "Tạm ngưng gian hàng",
                _ => throw new ShopRuleException("The store status cannot be changed.")
            };
            seller.Status = status;
            db.Audit(actor.UserId, action, Key(sellerId), time.GetUtcNow(), reason, sellerId);
            return true;
        });
    }

    public Task<SellerWorkspace> Workspace(ShopActor actor, long sellerId) =>
        store.Read(db => SellerAccess.Require(db, actor, sellerId, SellerPermission.Workspace));

    public Task<SellerTeam> Team(ShopActor actor, long sellerId) => store.Write(async db =>
    {
        await SellerAccess.Require(db, actor, sellerId, SellerPermission.ManageTeam);
        var members = await db.List(db.Query<SellerMembership>().Where(m => m.SellerId == sellerId)
            .OrderBy(m => m.Status).ThenBy(m => m.Role).ThenBy(m => m.Email)
            .Select(m => new SellerTeamMember(m.UserId, m.Email, m.Role, m.Status)));
        var now = time.GetUtcNow();
        var invites = await db.List(db.Query<SellerInvitation>().Where(i => i.SellerId == sellerId &&
                i.AcceptedAt == null && i.RevokedAt == null && i.ExpiresAt > now)
            .OrderBy(i => i.ExpiresAt).ThenBy(i => i.Id)
            .Select(i => new SellerInvitationInfo(i.Id, i.Email, i.Role, i.ExpiresAt)));
        return new SellerTeam(members, invites);
    });

    public Task<bool> Invite(ShopActor actor, long sellerId, string email, SellerRole role, string publicOrigin) => store.Write(async db =>
    {
        var seller = await SellerAccess.Require(db, actor, sellerId, SellerPermission.ManageTeam);
        email = ShopRules.Text(email, 256, "Email");
        if (!new EmailAddressAttribute().IsValid(email)) throw new ShopRuleException("The email address is invalid.");
        if (role is not (SellerRole.CatalogManager or SellerRole.Fulfillment or SellerRole.Finance))
            throw new ShopRuleException("The invited role is invalid.");
        if (publicOrigin?.Length is not (> 0 and <= 512) || !Uri.TryCreate(publicOrigin, UriKind.Absolute, out var origin) ||
            origin.Scheme != Uri.UriSchemeHttps || origin.AbsolutePath != "/" || origin.Query.Length != 0 ||
            origin.Fragment.Length != 0 || origin.UserInfo.Length != 0)
            throw new ShopRuleException("An HTTPS store address must be configured to send email.");

        var normalizedEmail = NormalizeEmail(email);
        if (await db.First(db.Query<SellerMembership>().Where(m => m.SellerId == sellerId &&
                m.Email == email && m.Status == SellerMembershipStatus.Active)) is not null)
            throw new ShopRuleException("This email already belongs to an active member.");

        var now = time.GetUtcNow();
        var previous = await db.List(db.Query<SellerInvitation>().Where(i => i.SellerId == sellerId &&
            i.NormalizedEmail == normalizedEmail && i.AcceptedAt == null && i.RevokedAt == null && i.ExpiresAt > now));
        foreach (var old in previous) old.RevokedAt = now;

        var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        var invitation = new SellerInvitation
        {
            SellerId = sellerId, Email = email, NormalizedEmail = normalizedEmail, Role = role,
            TokenHash = Hash(token), ExpiresAt = now.AddDays(7)
        };
        db.Add(invitation);
        await db.Save();
        var link = new Uri(origin, $"/seller/invitations/accept?token={token}");
        db.Add(new OutboxMessage
        {
            EventKey = $"seller-invitation:{Key(invitation.Id)}", Recipient = email, Subject = "Invitation to join an HKShop store",
            Body = $"You are invited to join the store {seller.DisplayName}. Open the link within 7 days:\n{link}", NextAttemptAt = now
        });
        db.Audit(actor.UserId, "Mời thành viên gian hàng", Key(invitation.Id), now, sellerId: sellerId);
        return true;
    });

    public Task<long> Accept(ShopActor actor, string token)
    {
        SellerAccess.RequireMfa(actor);
        if (token?.Length != 64 || token.Any(c => !char.IsAsciiHexDigit(c))) throw InvalidInvitation();
        var hash = Hash(token);
        var email = NormalizeEmail(actor.Email);
        return store.Write(async db =>
        {
            var now = time.GetUtcNow();
            var invitation = await db.First(db.Query<SellerInvitation>().Where(i => i.TokenHash == hash));
            if (invitation is null || invitation.AcceptedAt != null || invitation.RevokedAt != null ||
                invitation.ExpiresAt <= now || !string.Equals(invitation.NormalizedEmail, email, StringComparison.Ordinal) ||
                await db.First(db.Query<Seller>().Where(s => s.Id == invitation.SellerId && s.Status == SellerStatus.Active)) is null)
                throw InvalidInvitation();

            var membership = await FindMember(db, invitation.SellerId, actor.UserId);
            if (membership?.Status == SellerMembershipStatus.Active) throw InvalidInvitation();
            if (membership is null)
            {
                membership = new SellerMembership { SellerId = invitation.SellerId, UserId = actor.UserId };
                db.Add(membership);
            }
            membership.Email = actor.Email;
            membership.Role = invitation.Role;
            membership.Status = SellerMembershipStatus.Active;
            invitation.AcceptedAt = now;
            db.Audit(actor.UserId, "Chấp nhận lời mời gian hàng", Key(invitation.Id), now, sellerId: invitation.SellerId);
            return invitation.SellerId;
        });
    }

    public Task<bool> RevokeMember(ShopActor actor, long sellerId, string userId) => store.Write(async db =>
    {
        await SellerAccess.Require(db, actor, sellerId, SellerPermission.ManageTeam);
        var membership = await FindMember(db, sellerId, userId) ?? throw new SellerAccessDeniedException();
        if (membership.Role == SellerRole.Owner) throw new ShopRuleException("The store owner cannot be revoked.");
        if (membership.Status == SellerMembershipStatus.Revoked) return false;

        var now = time.GetUtcNow();
        membership.Status = SellerMembershipStatus.Revoked;
        db.Audit(actor.UserId, "Thu hồi quyền thành viên gian hàng", membership.UserId[..Math.Min(membership.UserId.Length, 150)],
            now, sellerId: sellerId);
        return true;
    });

    public Task<bool> RevokeInvitation(ShopActor actor, long sellerId, long invitationId) => store.Write(async db =>
    {
        await SellerAccess.Require(db, actor, sellerId, SellerPermission.ManageTeam);
        var invitation = await db.First(db.Query<SellerInvitation>().Where(i => i.SellerId == sellerId && i.Id == invitationId))
            ?? throw new SellerAccessDeniedException();
        var now = time.GetUtcNow();
        if (invitation.AcceptedAt != null || invitation.RevokedAt != null || invitation.ExpiresAt <= now) return false;
        invitation.RevokedAt = now;
        db.Audit(actor.UserId, "Thu hồi lời mời gian hàng", Key(invitationId), now, sellerId: sellerId);
        return true;
    });

    private static Task<SellerMembership?> FindMember(IShopSession db, long sellerId, string userId) =>
        db.First(db.Query<SellerMembership>().Where(m => m.SellerId == sellerId && m.UserId == userId));

    private static ShopRuleException InvalidInvitation() => new("The invitation is invalid or cannot be used with this account.");
    private static string NormalizeEmail(string email) => email.Trim().Normalize().ToUpperInvariant();
    private static string Hash(string token) => Convert.ToHexString(SHA256.HashData(Encoding.ASCII.GetBytes(token)));
    private static string Key(long id) => id.ToString(CultureInfo.InvariantCulture);
}
