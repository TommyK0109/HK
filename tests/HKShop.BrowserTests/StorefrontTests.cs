using System.Text.RegularExpressions;
using Microsoft.Playwright;
using Microsoft.Playwright.Xunit;
using Xunit;

namespace HKShop.BrowserTests;

public sealed class StorefrontTests : PageTest
{
    public override BrowserNewContextOptions ContextOptions() => new()
    {
        IgnoreHTTPSErrors = true,
        ViewportSize = new ViewportSize { Width = 360, Height = 800 }
    };

    [BrowserFact]
    public async Task GuestCanBrowseAndFindAccountRegistration()
    {
        var url = Environment.GetEnvironmentVariable("HKSHOP_BROWSER_URL")!.TrimEnd('/');
        var header = Page.Locator(".landing-header");
        await Page.GotoAsync(url + "/");
        await Expect(Page.GetByRole(AriaRole.Heading, new() { NameRegex = new Regex("^New collection$", RegexOptions.IgnoreCase) })).ToBeVisibleAsync();
        await Expect(Page.GetByRole(AriaRole.Link, new() { Name = "Shopping cart", Exact = true })).ToHaveAttributeAsync("href", "/cart");
        await Expect(Page.GetByRole(AriaRole.Search)).ToHaveCountAsync(0);
        Assert.True(await Page.EvaluateAsync<bool>("document.documentElement.scrollWidth <= window.innerWidth"));
        await Page.SetViewportSizeAsync(1280, 900);
        Assert.True(await Page.EvaluateAsync<bool>("document.documentElement.scrollWidth <= window.innerWidth"));
        await Page.SetViewportSizeAsync(360, 800);
        await Page.GetByRole(AriaRole.Link, new() { NameRegex = new Regex("^Shop now$", RegexOptions.IgnoreCase) }).ClickAsync();
        await Expect(Page.GetByRole(AriaRole.Heading, new() { Name = "Products", Exact = true })).ToBeVisibleAsync();
        await Expect(header).ToBeVisibleAsync();
        await Expect(Page.GetByRole(AriaRole.Search)).ToBeVisibleAsync();
        Assert.True(await Page.EvaluateAsync<bool>("document.documentElement.scrollWidth <= window.innerWidth"));
        await header.GetByRole(AriaRole.Link, new() { Name = "Login / Register", Exact = true }).ClickAsync();
        await Page.GetByRole(AriaRole.Link, new() { Name = "Create account", Exact = true }).ClickAsync();
        await Expect(header).ToBeVisibleAsync();
        await Expect(Page.GetByLabel("Email", new() { Exact = true })).ToBeVisibleAsync();
    }

    [BrowserFact]
    public async Task SePayCallbackWithoutCredentialsReturnsUnauthorized()
    {
        var url = Environment.GetEnvironmentVariable("HKSHOP_BROWSER_URL")!.TrimEnd('/');
        var response = await Page.APIRequest.PostAsync(url + "/payments/sepay/ipn", new() { DataObject = new { } });
        Assert.Equal(401, response.Status);
    }
}

public sealed class BrowserFactAttribute : FactAttribute
{
    public BrowserFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("HKSHOP_BROWSER_URL")))
            Skip = "Set HKSHOP_BROWSER_URL to a running development or staging site.";
    }
}
