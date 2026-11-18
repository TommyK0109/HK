using System.Net;
using System.Security.Claims;
using System.Text.RegularExpressions;
using HKShop.Infrastructure.Persistence;
using HKShop.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.Mvc.Testing.Handlers;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Xunit;

namespace HKShop.IntegrationTests;

internal sealed class PlatformHttpTestHost(string? connection, bool enableTransport) : WebApplicationFactory<ShopFailureFilter>
{
    public const string BankWebhookKey = "platform-http-tests-bank-key";
    public const string BankAccount = "123456";
    private static readonly Uri Origin = new("https://localhost");
    private readonly DirectoryInfo files = Directory.CreateTempSubdirectory("HKShop-Platform-");

    public static PlatformHttpTestHost Create(string? connection = null, bool enableTransport = false) =>
        new(connection, enableTransport);

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder
            .UseSetting("ConnectionStrings:HKShop", connection ?? "Server=127.0.0.1,1;Database=HKShopUnreachable;Connect Timeout=1")
            .UseSetting("Shop:KeysPath", Path.Combine(files.FullName, "keys"))
            .UseSetting("Shop:MediaPath", Path.Combine(files.FullName, "media"))
            .UseSetting("Shop:PublicOrigin", Origin.GetLeftPart(UriPartial.Authority))
            .UseSetting("S3:Enabled", "false")
            .UseSetting("Kafka:BootstrapServers", "127.0.0.1:1")
            .UseSetting("Kafka:CheckoutTopic", "platform-http-tests")
            .UseSetting("Kafka:CheckoutGroupId", "platform-http-tests")
            .UseSetting("Transport:Enabled", enableTransport ? "true" : "false")
            .UseSetting("Transport:RoutingBaseUrl", "http://127.0.0.1:1")
            .UseSetting("Transport:BankName", "Test bank")
            .UseSetting("Transport:BankAccount", BankAccount)
            .UseSetting("Transport:BankWebhookKey", BankWebhookKey)
            .ConfigureTestServices(services => services.RemoveAll<IHostedService>());
    }

    public HttpClient CreateBrowser() =>
        CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = Origin, AllowAutoRedirect = false });

    public async Task<HttpClient> CreateBrowserAsync(string userId, bool mfa = true)
    {
        using var scope = Services.CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<ShopUser>>();
        var user = await users.FindByIdAsync(userId);
        Assert.NotNull(user);
        var principalFactory = scope.ServiceProvider.GetRequiredService<IUserClaimsPrincipalFactory<ShopUser>>();
        var principal = await principalFactory.CreateAsync(user);
        if (mfa) ((ClaimsIdentity)principal.Identity!).AddClaim(new Claim("amr", "mfa"));

        var options = scope.ServiceProvider.GetRequiredService<IOptionsMonitor<CookieAuthenticationOptions>>()
            .Get(IdentityConstants.ApplicationScheme);
        var now = DateTimeOffset.UtcNow;
        var properties = new AuthenticationProperties { IssuedUtc = now, ExpiresUtc = now.AddHours(1) };
        var ticket = new AuthenticationTicket(principal, properties, IdentityConstants.ApplicationScheme);
        var cookies = new CookieContainer();
        cookies.Add(Origin, new Cookie(options.Cookie.Name!, options.TicketDataFormat.Protect(ticket)));
        return CreateDefaultClient(Origin, new CookieContainerHandler(cookies));
    }

    public static string FormValue(string html, string name)
    {
        var field = Regex.Match(html, $"""<input\b[^>]*\bname="{Regex.Escape(name)}"[^>]*\bvalue="([^"]*)"[^>]*>""");
        Assert.True(field.Success, $"Missing form field {name}.");
        return WebUtility.HtmlDecode(field.Groups[1].Value);
    }

    public static async Task<HttpResponseMessage> PostFormAsync(
        HttpClient client, string path, string sourcePath, Dictionary<string, string> fields)
    {
        var html = await client.GetStringAsync(sourcePath);
        fields["__RequestVerificationToken"] = FormValue(html, "__RequestVerificationToken");
        using var request = new HttpRequestMessage(HttpMethod.Post, path) { Content = new FormUrlEncodedContent(fields) };
        request.Headers.Referrer = new Uri(client.BaseAddress!, sourcePath);
        return await client.SendAsync(request);
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing && files.Exists) files.Delete(recursive: true);
    }
}
