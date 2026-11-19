using HKShop.Application;
using HKShop.Application.Sellers;
using HKShop.Contracts.Ports;
using Microsoft.AspNetCore.DataProtection;

namespace HKShop.IntegrationTests;

internal static class StoreApplicationFixture
{
    public static StoreApplications Service(IShopStore store, TimeProvider clock) =>
        new(store, clock, new EphemeralDataProtectionProvider());

    public static Task<Guid> Submit(StoreApplications applications, ShopActor owner, string name) =>
        applications.Submit(owner, Guid.NewGuid(), name, "TEST-ID", "TEST-LICENSE", [new("Test product", 10_000)],
            Enum.GetValues<StoreDocumentKind>().ToDictionary(kind => kind, _ => new StoreApplicationDocument([1], "image/png")));

    public static async Task<long> Open(IShopStore store, TimeProvider clock, ShopActor reviewer, ShopActor owner, string name)
    {
        var applications = Service(store, clock);
        var id = await Submit(applications, owner, name);
        await applications.Decide(reviewer, id, true, null);
        return (await applications.Details(reviewer, id))!.Summary.SellerId!.Value;
    }
}
