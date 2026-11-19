using System.Globalization;
using HKShop.Application;
using HKShop.Application.Catalog;
using HKShop.Application.Checkout;
using HKShop.Application.Currencies;
using HKShop.Application.Orders;
using HKShop.Application.Payments;
using HKShop.Application.Reporting;
using HKShop.Application.Sellers;
using HKShop.Application.Settlement;
using HKShop.Application.Transportation;
using HKShop.Commerce;
using HKShop.Commerce.Application.Ports;
using HKShop.Contracts.Ports;
using HKShop.EOffice;
using HKShop.EOffice.Shipping.Application;
using HKShop.Infrastructure.BackgroundJobs;
using HKShop.Infrastructure.Email;
using HKShop.Infrastructure.Identity;
using HKShop.Infrastructure.Kafka;
using HKShop.Infrastructure.Media;
using HKShop.Infrastructure.Payments;
using HKShop.Infrastructure.Persistence;
using HKShop.Infrastructure.Transportation;
using HKShop.ServiceDefaults;
using HKShop.Web;
using HKShop.Web.Components;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);
builder.AddHKShopDefaults("HKShop");
var shop = builder.Configuration.GetSection("Shop").Get<ShopSettings>() ?? new();
shop.MediaPath = Path.GetFullPath(shop.MediaPath, builder.Environment.ContentRootPath);
shop.KeysPath = Path.GetFullPath(shop.KeysPath, builder.Environment.ContentRootPath);
Directory.CreateDirectory(shop.KeysPath);
builder.Services.AddSingleton(shop);
builder.Services.AddSingleton(builder.Configuration.GetSection("SePay").Get<SePaySettings>() ?? new());
builder.Services.AddSingleton(builder.Configuration.GetSection("Smtp").Get<SmtpSettings>() ?? new());
var transport = builder.Configuration.GetSection("Transport").Get<TransportSettings>() ?? new();
if (transport.Enabled &&
    (!Uri.TryCreate(transport.RoutingBaseUrl, UriKind.Absolute, out var routeUri) ||
     routeUri.Scheme is not ("http" or "https") || routeUri.Host.Length == 0 || string.IsNullOrWhiteSpace(transport.BankName) ||
     string.IsNullOrWhiteSpace(transport.BankAccount) || transport.BankWebhookKey.Length < 24))
    throw new InvalidOperationException("Configure the OSRM URL, bank, account number, and a bank webhook key before enabling transportation.");
builder.Services.AddSingleton(transport);
var s3Media = builder.Configuration.GetSection("S3").Get<S3MediaSettings>() ?? new();
builder.Services.AddSingleton(s3Media);
builder.Services.AddSingleton(TimeProvider.System);

var connection = builder.Configuration.GetConnectionString("HKShop")
    ?? throw new InvalidOperationException("Configure ConnectionStrings:HKShop before starting the application.");
builder.Services.AddDbContextFactory<ShopDbContext>(options => options.UseSqlServer(connection));
builder.Services.AddIdentity<ShopUser, IdentityRole>(options =>
{
    options.User.RequireUniqueEmail = true;
    options.SignIn.RequireConfirmedEmail = true;
    options.Password.RequiredLength = 10;
    options.Password.RequireDigit = false;
    options.Password.RequireNonAlphanumeric = false;
    options.Password.RequireLowercase = false;
    options.Password.RequireUppercase = false;
    options.Lockout.MaxFailedAccessAttempts = 5;
}).AddEntityFrameworkStores<ShopDbContext>().AddDefaultTokenProviders();
builder.Services.ConfigureApplicationCookie(options =>
{
    options.SlidingExpiration = false;
    options.ExpireTimeSpan = TimeSpan.FromHours(8);
    options.LoginPath = "/account/login";
    options.AccessDeniedPath = "/account/denied";
    options.Cookie.HttpOnly = true;
    options.Cookie.SecurePolicy = CookieSecurePolicy.Always;
    options.Cookie.SameSite = SameSiteMode.Lax;
    options.Events.OnRedirectToLogin = context =>
    {
        var path = context.Request.Path;
        var loginBase = path.StartsWithSegments("/eoffice") || path.StartsWithSegments("/api/eoffice")
            ? "/eoffice/login"
            : "/account/login";
        var returnUrl = context.Request.PathBase + context.Request.Path + context.Request.QueryString;
        context.Response.Redirect(QueryHelpers.AddQueryString(loginBase, "returnUrl", returnUrl));
        return Task.CompletedTask;
    };
    options.Events.OnRedirectToAccessDenied = context =>
    {
        var path = context.Request.Path;
        var deniedBase = path.StartsWithSegments("/eoffice") || path.StartsWithSegments("/api/eoffice")
            ? "/eoffice/denied"
            : "/account/denied";
        var returnUrl = context.Request.PathBase + context.Request.Path + context.Request.QueryString;
        context.Response.Redirect(QueryHelpers.AddQueryString(deniedBase, "returnUrl", returnUrl));
        return Task.CompletedTask;
    };
    if (!string.IsNullOrWhiteSpace(builder.Configuration["Services:Identity:BaseUrl"]) &&
        builder.Configuration.GetValue("Identity:UseJwtAuth", false))
    {
        options.Events.OnValidatePrincipal = _ => Task.CompletedTask;
    }
});
builder.Services.Configure<SecurityStampValidatorOptions>(options =>
{
    options.ValidationInterval = TimeSpan.Zero;
    options.OnRefreshingPrincipal = context =>
    {
        if (context.CurrentPrincipal?.HasClaim("amr", "mfa") == true &&
            context.NewPrincipal?.Identity is System.Security.Claims.ClaimsIdentity identity)
            identity.AddClaim(new System.Security.Claims.Claim("amr", "mfa"));
        return Task.CompletedTask;
    };
});

builder.Services.AddDataProtection().SetApplicationName("HKShop").PersistKeysToFileSystem(new DirectoryInfo(shop.KeysPath));
builder.Services.AddScoped<IShopUserResolver, ShopUserResolver>();
builder.Services.AddScoped<ShopRequest>();
builder.Services.AddScoped<IShopRequestContext>(sp => sp.GetRequiredService<ShopRequest>());
builder.Services.AddScoped<ICommerceAuthService, CommerceAuthService>();
builder.Services.AddScoped<IEOfficeAuthService, EOfficeAuthService>();
builder.Services.AddScoped<HKShop.Application.Accounts.IStaffDirectory, StaffDirectory>();
builder.Services.AddScoped<HKShop.Application.Accounts.IUserProfiles, IdentityUserProfiles>();
builder.Services.AddScoped<HKShop.Web.Controllers.CheckoutTokens>();
builder.Services.AddScoped<IShopStore, SqlShopStore>();

// Commerce services & port adapters
builder.Services.AddScoped<CatalogService>();
builder.Services.AddScoped<InventoryService>();
builder.Services.AddScoped<CartService>(sp =>
    new CartService(sp.GetRequiredService<IShopStore>(), sp.GetRequiredService<TimeProvider>()));
builder.Services.AddScoped<AddressService>(sp =>
    new AddressService(sp.GetRequiredService<IShopStore>()));
builder.Services.AddScoped<PromotionService>();
builder.Services.AddScoped<CheckoutService>(sp =>
    new CheckoutService(
        sp.GetRequiredService<IShopStore>(),
        sp.GetRequiredService<ShopSettings>(),
        sp.GetRequiredService<TimeProvider>(),
        sp.GetService<TransportSettings>(),
        sp.GetService<ITransportRouting>(),
        sp.GetService<IShippingGateway>()));
builder.Services.AddScoped<OrderService>(sp =>
    new OrderService(
        sp.GetRequiredService<IShopStore>(),
        sp.GetRequiredService<TimeProvider>(),
        sp.GetService<IShippingGateway>()));
builder.Services.AddScoped<PaymentService>(sp =>
    new PaymentService(
        sp.GetRequiredService<IShopStore>(),
        sp.GetRequiredService<IPaymentGateway>(),
        sp.GetRequiredService<TimeProvider>(),
        sp.GetService<IShippingGateway>()));
builder.Services.AddScoped<RefundWorkflow>();
builder.Services.AddScoped<IRefundGateway, SePayRefundGateway>();
builder.Services.AddScoped<ReturnService>();
builder.Services.AddScoped<ReportingService>();
builder.Services.AddScoped<SellerService>();
builder.Services.AddScoped<StoreApplications>();
builder.Services.AddScoped<IStoreApplicationReview>(sp => sp.GetRequiredService<StoreApplications>());
builder.Services.AddScoped<SellerCatalogService>();
builder.Services.AddScoped<SettlementLedger>();
builder.Services.AddScoped<PayoutService>();
builder.Services.AddScoped<IPayoutGateway, SimulatedPayoutGateway>();
builder.Services.AddSingleton(builder.Configuration.GetSection("PayoutSimulation").Get<PayoutSimulationSettings>() ?? new());
builder.Services.AddScoped<ICatalogOperationsPort, CatalogOperationsAdapter>();
builder.Services.AddScoped<ICommerceOperationsPort, CommerceOperationsAdapter>();
builder.Services.AddScoped<IShippingCommerceCallbackPort, ShippingCommerceCallbackAdapter>();

// EOffice Shipping services & port adapters
builder.Services.AddScoped<TransportService>();
builder.Services.AddScoped<ShippingGatewayAdapter>();
builder.Services.AddScoped<IShippingGateway>(sp => sp.GetRequiredService<ShippingGatewayAdapter>());
builder.Services.AddScoped<IShippingAdminPort>(sp => sp.GetRequiredService<ShippingGatewayAdapter>());
builder.Services.AddScoped<IShippingShipperPort>(sp => sp.GetRequiredService<ShippingGatewayAdapter>());
builder.Services.AddHttpClient<ITransportRouting, OsrmRouting>(client => client.Timeout = TimeSpan.FromSeconds(4));

builder.Services.AddSingleton<ICurrencyExchangeRateProvider, DefaultExchangeRateProvider>();
builder.Services.AddScoped<CurrencyExchangeService>();
builder.Services.AddSingleton<SmtpSender>();
if (s3Media.Enabled)
{
    builder.Services.AddSingleton<Amazon.S3.IAmazonS3>(_ => S3ProductImages.CreateClient(s3Media));
    builder.Services.AddSingleton<IProductImageStore>(sp =>
        new S3ProductImages(sp.GetRequiredService<Amazon.S3.IAmazonS3>(), s3Media));
}
else
{
    builder.Services.AddSingleton<IProductImageStore, LocalProductImages>();
}
builder.Services.AddHttpClient<SePayGateway>(client => client.Timeout = TimeSpan.FromSeconds(15))
    .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false });
builder.Services.AddScoped<IPaymentGateway>(s => s.GetRequiredService<SePayGateway>());
builder.Services.AddScoped<ISePayWebhookHandler>(s => s.GetRequiredService<SePayGateway>());

if (string.IsNullOrWhiteSpace(builder.Configuration["Services:Payments:BaseUrl"]) &&
    string.IsNullOrWhiteSpace(builder.Configuration["Services:Ordering:BaseUrl"]))
    builder.Services.AddHostedService<CommerceMaintenanceWorker>();
if (string.IsNullOrWhiteSpace(builder.Configuration["Services:Shipping:BaseUrl"]))
    builder.Services.AddHostedService<ShippingDispatchWorker>();
if (!builder.Configuration.GetValue("Notification:RetireLegacyEmailWorker", false))
    builder.Services.AddHostedService<EmailOutboxWorker>();
if (string.IsNullOrWhiteSpace(builder.Configuration["Services:Ordering:BaseUrl"]))
{
    builder.Services.AddKafkaCheckout(builder.Configuration);
    builder.Services.AddScoped<ICheckoutRequestDispatcher>(sp => sp.GetRequiredService<CheckoutRequests>());
}
builder.Services.AddHKShopBffClients(builder.Configuration);
builder.Services.AddHealthChecks().AddHKShopDbReadiness<ShopDbContext>("sql");
builder.Services.AddRazorComponents().AddInteractiveServerComponents();
builder.Services.AddCascadingAuthenticationState();
builder.Services.AddControllersWithViews(options =>
{
    options.Filters.Add(new Microsoft.AspNetCore.Mvc.AutoValidateAntiforgeryTokenAttribute());
    options.Filters.Add<ShopFailureFilter>();
})
.AddApplicationPart(typeof(CommerceAssemblyMarker).Assembly)
.AddApplicationPart(typeof(EOfficeAssemblyMarker).Assembly);

CultureInfo.DefaultThreadCurrentCulture = CultureInfo.GetCultureInfo("vi-VN");
CultureInfo.DefaultThreadCurrentUICulture = CultureInfo.GetCultureInfo("en");

var app = builder.Build();
app.UseForwardedHeaders();
app.UseExceptionHandler("/error", createScopeForErrors: true);
if (!app.Environment.IsDevelopment()) app.UseHsts();
if (app.Configuration.GetValue("HttpsRedirection:Enabled", true))
    app.UseHttpsRedirection();
app.UseHKShopSecurityHeaders();
app.UseStatusCodePagesWithReExecute("/status/{0}", createScopeForStatusCodePages: true);
app.UseHKShopRequestLogging();
app.UseAuthentication();
app.UseAuthorization();
app.UseRateLimiter();
app.UseAntiforgery();
app.MapStaticAssets();
app.MapControllers();
app.MapRazorComponents<App>()
    .AddAdditionalAssemblies(typeof(CommerceAssemblyMarker).Assembly, typeof(EOfficeAssemblyMarker).Assembly)
    .AddInteractiveServerRenderMode();
app.MapHKShopHealthChecks();
app.MapHKShopBffCompositionEndpoints();
app.MapGet("/media/{name}", async (string name, IProductImageStore media, HttpContext context, CancellationToken cancellationToken) =>
{
    var image = await media.OpenRead(name, cancellationToken);
    if (image is null) return Results.NotFound();
    context.Response.RegisterForDisposeAsync(image);
    context.Response.Headers.CacheControl = ImageUpload.ImmutableCacheControl;
    return Results.Stream(image.Content, image.ContentType);
}).AllowAnonymous();

if (args.Contains("--bootstrap-admin", StringComparer.Ordinal))
{
    await using var scope = app.Services.CreateAsyncScope();
    await AdminBootstrap.Run(scope.ServiceProvider, builder.Configuration);
    return;
}
app.Run();
