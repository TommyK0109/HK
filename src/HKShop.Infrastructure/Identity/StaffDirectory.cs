using System.ComponentModel.DataAnnotations;
using System.Data;
using HKShop.Application;
using HKShop.Application.Accounts;
using HKShop.Contracts.Ports;
using HKShop.Domain;
using HKShop.Domain.Orders;
using HKShop.Infrastructure.Persistence;
using Microsoft.AspNetCore.Identity;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;

namespace HKShop.Infrastructure.Identity;

public sealed class StaffDirectory(UserManager<ShopUser> users, ShopDbContext db, TimeProvider time,
    IShippingCommerceCallbackPort commerce) : IStaffDirectory
{
    public async Task<Page<StaffAccount>> Browse(ShopActor actor, string? search, int page)
    {
        actor.RequireAdmin();
        var keyword = (search ?? "").Trim();
        keyword = keyword[..Math.Min(keyword.Length, 100)];
        var query = users.Users.AsNoTracking().Where(u => keyword == "" ||
            (u.Email != null && u.Email.Contains(keyword)) || u.DisplayName.Contains(keyword));
        var total = await query.CountAsync();
        var number = Math.Clamp(page, 1, Math.Max(1, (int)Math.Ceiling(total / 25d)));
        var accounts = await query.OrderBy(u => u.NormalizedEmail).ThenBy(u => u.Id).Skip((number - 1) * 25).Take(25).ToListAsync();
        var rows = new List<StaffAccount>();
        foreach (var user in accounts)
        {
            var roles = await users.GetRolesAsync(user);
            var roleName = roles.Contains("Admin")
                ? "Admin"
                : roles.Contains("StoreStaff")
                    ? "StoreStaff"
                    : roles.Contains("Shipper")
                        ? "Shipper"
                        : "Customer";
            rows.Add(new StaffAccount(user.Id, user.DisplayName, user.Email, user.EmailConfirmed, user.TwoFactorEnabled,
                user.Disabled, roleName, await users.HasPasswordAsync(user)));
        }
        return new Page<StaffAccount>(rows, number, total, 25);
    }

    public async Task<StaffInvitation> Invite(ShopActor actor, string email, string name, string role)
    {
        actor.RequireAdmin();
        email = ShopRules.Text(email, 256, "Email");
        if (!new EmailAddressAttribute().IsValid(email)) throw new ShopRuleException("Enter a valid email address.");
        RequireRole(role);
        var user = new ShopUser { Email = email, UserName = email, DisplayName = ShopRules.Text(name, 100, "Full name") };
        try
        {
            await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable);
            Require(await users.CreateAsync(user));
            Require(await users.AddToRoleAsync(user, role));
            if (role == "Shipper")
                db.Add(new TransportShipper { UserId = user.Id, Email = email, Active = true });
            db.Add(new AuditEntry { ActorId = actor.UserId, Action = "Mời nhân viên", Target = user.Id,
                Reason = role, OccurredAt = time.GetUtcNow() });
            await db.SaveChangesAsync();
            await transaction.CommitAsync();
        }
        catch (DbUpdateException error) when (error.InnerException is SqlException { Number: 2601 or 2627 })
        { throw new ShopRuleException("Email is already in use."); }
        return await Invitation(user);
    }

    public async Task<StaffInvitation> GetPendingInvitation(ShopActor actor, string id)
    {
        actor.RequireAdmin();
        var user = await users.FindByIdAsync(id);
        if (user is null || user.Disabled || await users.HasPasswordAsync(user) ||
            (!await users.IsInRoleAsync(user, "Admin") &&
             !await users.IsInRoleAsync(user, "StoreStaff") &&
             !await users.IsInRoleAsync(user, "Shipper")))
            throw new ShopRuleException("Account in pending.");
        return await Invitation(user);
    }

    public async Task ChangeAccess(ShopActor actor, string id, string role, bool disabled)
    {
        actor.RequireAdmin();
        if (role != "Customer") RequireRole(role);
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable);
        var user = await users.FindByIdAsync(id) ?? throw new ShopRuleException("Cannot find account.");
        var existing = await users.GetRolesAsync(user);
        if (role == "Shipper" && (!disabled || !existing.Contains(role)))
            await commerce.RequireShipperEligibilityAsync(new SqlShopStore.Session(db), id);
        if (existing.Contains("Admin") && !user.Disabled && (disabled || role != "Admin") &&
            (await users.GetUsersInRoleAsync("Admin")).Count(u => !u.Disabled) <= 1)
            throw new ShopRuleException("Admin not allowed to be disabled.");
        foreach (var current in existing.Where(r => r is "Admin" or "StoreStaff" or "Shipper").Where(r => r != role))
            Require(await users.RemoveFromRoleAsync(user, current));
        if (role != "Customer" && !existing.Contains(role)) Require(await users.AddToRoleAsync(user, role));
        user.Disabled = disabled;
        Require(await users.UpdateAsync(user));
        Require(await users.UpdateSecurityStampAsync(user));

        var shipperRow = await db.Set<TransportShipper>().FirstOrDefaultAsync(s => s.UserId == user.Id);
        if (role == "Shipper" && !disabled)
        {
            if (shipperRow is null)
            {
                shipperRow = new TransportShipper { UserId = user.Id, Email = user.Email ?? "", Active = true };
                db.Add(shipperRow);
            }
            else
            {
                shipperRow.Email = user.Email ?? "";
                shipperRow.Active = true;
            }
        }
        else if (shipperRow is not null && shipperRow.Active)
        {
            var hasActiveAssignment = await db.Set<TransportAssignment>()
                .AnyAsync(a => a.ShipperId == user.Id && a.EndedAt == null);
            if (hasActiveAssignment)
                throw new ShopRuleException("Resolve the shipper's active assignment before disabling their account.");
            shipperRow.Active = false;
            shipperRow.OnDuty = false;
        }

        db.Add(new AuditEntry { ActorId = actor.UserId, Action = "Update user", Target = user.Id,
            Reason = $"{role}, {(disabled ? "disabled" : "active")}", OccurredAt = time.GetUtcNow() });
        await db.SaveChangesAsync();
        await transaction.CommitAsync();
    }

    private async Task<StaffInvitation> Invitation(ShopUser user) => new(user.Id, user.Email!,
        await users.GeneratePasswordResetTokenAsync(user), await users.GenerateEmailConfirmationTokenAsync(user));

    private static void RequireRole(string role)
    {
        if (role is not ("StoreStaff" or "Admin" or "Shipper")) throw new ShopRuleException("Invalid Role.");
    }

    private static void Require(IdentityResult result)
    {
        if (result.Succeeded) return;
        throw new ShopRuleException(result.Errors.First().Code is "DuplicateEmail" or "DuplicateUserName"
            ? "Email already in use." : "Cannot update account. Please check and try again.");
    }
}
