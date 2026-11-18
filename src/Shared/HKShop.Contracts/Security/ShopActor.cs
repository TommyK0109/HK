using HKShop.Domain;

namespace HKShop.Application;

public sealed record ShopActor(
    string UserId, string Email, bool Verified,
    bool Staff, bool Admin, bool Shipper = false)
{
    public bool Mfa { get; init; }
    public bool HasEmployeeRole { get; init; }
    public bool IsEmployee => Staff || Admin || Shipper || HasEmployeeRole;

    public void RequireCustomer()
    {
        if (UserId.Length == 0 || !Verified)
            throw new ShopRuleException("Verify your email before continuing.");
        if (IsEmployee)
            throw new ShopRuleException("Employee and shipper accounts cannot use shopping features.");
    }

    public void RequireStaff()
    {
        if (!Staff && !Admin)
            throw new ShopRuleException("You cannot perform this action.");
    }

    public void RequireAdmin()
    {
        if (!Admin)
            throw new ShopRuleException("You do not have administrative privileges.");
    }

    public void RequireShipper()
    {
        if (!Shipper || !Mfa)
            throw new ShopRuleException("This account is not an active HKShop shipper with MFA.");
    }
}
