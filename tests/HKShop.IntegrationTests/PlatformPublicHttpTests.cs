using System.Net;
using System.Net.Http.Json;
using Xunit;

namespace HKShop.IntegrationTests;

public sealed class PlatformPublicHttpTests
{
    [Theory]
    [InlineData("/admin", "/eoffice")]
    [InlineData("/admin/orders?page=2&status=packed", "/eoffice/orders?page=2&status=packed")]
    [InlineData("/shipper?view=history", "/eoffice/shipper?view=history")]
    public async Task LegacyEntryRedirectsToEOffice(string path, string target)
    {
        using var app = PlatformHttpTestHost.Create();
        using var client = app.CreateBrowser();

        using var response = await client.GetAsync(path);

        Assert.InRange((int)response.StatusCode, 300, 399);
        var location = Assert.IsType<Uri>(response.Headers.Location);
        Assert.Equal(new Uri(client.BaseAddress!, target), new Uri(client.BaseAddress!, location));
    }

    [Fact]
    public async Task EachPublicLoginUsesItsOwnLayoutAndFormAction()
    {
        using var app = PlatformHttpTestHost.Create();
        using var client = app.CreateBrowser();

        var commerce = await client.GetStringAsync("/account/login");
        Assert.Contains("action=\"/api/commerce/account/login\"", commerce);
        Assert.Contains("aria-label=\"Shopping cart\"", commerce);
        Assert.DoesNotContain("aria-label=\"EOffice account\"", commerce);
        Assert.DoesNotContain("href=\"/eoffice", commerce);
        Assert.DoesNotContain("href=\"/admin", commerce);
        Assert.DoesNotContain("href=\"/shipper", commerce);

        var office = await client.GetStringAsync("/eoffice/login");
        Assert.Contains("action=\"/api/eoffice/account/login\"", office);
        Assert.Contains("aria-label=\"EOffice account\"", office);
        Assert.DoesNotContain("aria-label=\"Shopping cart\"", office);
        Assert.DoesNotContain("href=\"/products", office);
        Assert.DoesNotContain("href=\"/seller", office);
    }

    [Fact]
    public async Task SePayCallbackRejectsUnauthenticatedRequest()
    {
        using var app = PlatformHttpTestHost.Create();
        using var client = app.CreateBrowser();

        using var response = await client.PostAsJsonAsync("/payments/sepay/ipn", new { });

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }
}
