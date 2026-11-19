using System.Diagnostics;
using System.Net;
using System.Security.Claims;
using System.Threading.RateLimiting;
using HKShop.Domain;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using OpenTelemetry;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using Serilog;

namespace HKShop.ServiceDefaults;

public sealed class ShopRuleProblemDetailsHandler(IProblemDetailsService problemDetails, ILogger<ShopRuleProblemDetailsHandler> logger)
    : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(HttpContext httpContext, Exception exception, CancellationToken cancellationToken)
    {
        if (exception is not ShopRuleException ruleError)
            return false;

        logger.LogInformation("Shop rule rejected {Method} {Path} ({TraceId}).",
            httpContext.Request.Method, httpContext.Request.Path, httpContext.TraceIdentifier);

        httpContext.Response.StatusCode = StatusCodes.Status400BadRequest;
        var problem = new ProblemDetails
        {
            Status = StatusCodes.Status400BadRequest,
            Title = "Business rule violation",
            Detail = ruleError.Message,
            Type = "https://tools.ietf.org/html/rfc9110#section-15.5.1",
            Instance = httpContext.Request.Path
        };
        problem.Extensions["traceId"] = Activity.Current?.Id ?? httpContext.TraceIdentifier;

        if (!await problemDetails.TryWriteAsync(new ProblemDetailsContext
        {
            HttpContext = httpContext,
            Exception = exception,
            ProblemDetails = problem
        }))
        {
            await httpContext.Response.WriteAsJsonAsync(
                problem,
                options: null,
                contentType: "application/problem+json",
                cancellationToken: cancellationToken);
        }

        return true;
    }
}

public static class ServiceDefaultsExtensions
{
    public const string CustomerWritesPolicy = "CustomerWrites";

    public static TBuilder AddHKShopDefaults<TBuilder>(this TBuilder builder, string? serviceName = null)
        where TBuilder : IHostApplicationBuilder
    {
        var appName = string.IsNullOrWhiteSpace(serviceName)
            ? (string.IsNullOrWhiteSpace(builder.Environment.ApplicationName) ? "HKShop" : builder.Environment.ApplicationName)
            : serviceName;

        HKShopTelemetry.ConfigureW3CPropagation();

        builder.Services.AddSerilog((services, loggerConfig) =>
        {
            loggerConfig
                .ReadFrom.Configuration(builder.Configuration)
                .ReadFrom.Services(services)
                .Enrich.FromLogContext()
                .Enrich.WithProperty("Application", appName)
                .Enrich.WithProperty("Environment", builder.Environment.EnvironmentName);

            if (!builder.Configuration.GetSection("Serilog:WriteTo").Exists())
                loggerConfig.WriteTo.Console();

            var seqUrl = builder.Configuration["Seq:ServerUrl"];
            if (!string.IsNullOrWhiteSpace(seqUrl))
            {
                var seqApiKey = builder.Configuration["Seq:ApiKey"];
                loggerConfig.WriteTo.Seq(seqUrl, apiKey: string.IsNullOrWhiteSpace(seqApiKey) ? null : seqApiKey);
            }
        });

        var otel = builder.Services.AddOpenTelemetry()
            .ConfigureResource(resource => resource.AddService(appName))
            .WithTracing(tracing =>
            {
                tracing
                    .AddSource(HKShopTelemetry.ActivitySourceName, appName)
                    .AddAspNetCoreInstrumentation(options =>
                    {
                        options.RecordException = true;
                        options.Filter = context => !context.Request.Path.StartsWithSegments("/health");
                    })
                    .AddHttpClientInstrumentation(options => options.RecordException = true);
            })
            .WithMetrics(metrics =>
            {
                metrics
                    .AddMeter(HKShopTelemetry.MeterName, appName)
                    .AddAspNetCoreInstrumentation()
                    .AddHttpClientInstrumentation()
                    .AddRuntimeInstrumentation();
            });

        var otlpEndpoint = builder.Configuration["OpenTelemetry:OtlpEndpoint"]
                           ?? builder.Configuration["OTEL_EXPORTER_OTLP_ENDPOINT"];
        if (!string.IsNullOrWhiteSpace(otlpEndpoint))
            otel.UseOtlpExporter();

        builder.Services.AddProblemDetails(options =>
        {
            options.CustomizeProblemDetails = context =>
            {
                context.ProblemDetails.Extensions.TryAdd("traceId",
                    Activity.Current?.Id ?? context.HttpContext.TraceIdentifier);
            };
        });
        builder.Services.AddExceptionHandler<ShopRuleProblemDetailsHandler>();

        builder.Services.AddHKShopSecurity(builder.Configuration);
        builder.Services.AddHKShopHttpResilience(builder.Configuration);
        builder.Services.AddHealthChecks();

        var permitLimit = builder.Configuration.GetValue("RateLimiting:CustomerWritesPermitLimit", 30);
        builder.Services.AddRateLimiter(options =>
        {
            options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            options.AddPolicy(CustomerWritesPolicy, context => RateLimitPartition.GetFixedWindowLimiter(
                context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                _ => new FixedWindowRateLimiterOptions
                {
                    PermitLimit = Math.Max(1, permitLimit),
                    Window = TimeSpan.FromMinutes(1),
                    QueueLimit = 0
                }));
        });

        builder.Services.Configure<ForwardedHeadersOptions>(options =>
        {
            options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
            if (IPAddress.TryParse(builder.Configuration["Proxy:KnownProxy"], out var proxy))
                options.KnownProxies.Add(proxy);
        });

        return builder;
    }

    public static IApplicationBuilder UseHKShopSecurityHeaders(this IApplicationBuilder app) =>
        app.Use(static async (context, next) =>
        {
            context.Response.Headers["X-Content-Type-Options"] = "nosniff";
            context.Response.Headers["Referrer-Policy"] = "same-origin";
            if (IsSensitivePath(context.Request.Path))
                context.Response.Headers.CacheControl = "no-store";
            await next();
        });

    public static IApplicationBuilder UseHKShopRequestLogging(this WebApplication app)
    {
        var httpLogger = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("HKShop.Http");
        return app.Use(async (context, next) =>
        {
            var started = Stopwatch.GetTimestamp();
            var route = (context.GetEndpoint() as RouteEndpoint)?.RoutePattern.RawText;
            await next();
            var path = context.Request.Path;
            var isApi = path.StartsWithSegments("/api") || path.StartsWithSegments("/payments") || path.StartsWithSegments("/internal");
            if (!path.StartsWithSegments("/health") && (route is not null || isApi))
            {
                var statusCode = context.Response.StatusCode;
                var level = statusCode >= 500 ? LogLevel.Error
                    : statusCode >= 400 ? LogLevel.Warning
                    : LogLevel.Information;
                var userId = context.User.FindFirstValue("sub")
                             ?? context.User.FindFirstValue(ClaimTypes.NameIdentifier)
                             ?? "anonymous";
                httpLogger.Log(level,
                    "{LogSource} {Method} {Route} ({Path}) returned {StatusCode} in {ElapsedMs} ms for {UserId} ({TraceId}) [ReqBytes={RequestBytes}, ResType={ResponseContentType}].",
                    isApi ? "Api" : "Http", context.Request.Method, route ?? (path.Value ?? "/"), path.Value ?? "/",
                    statusCode, Math.Round(Stopwatch.GetElapsedTime(started).TotalMilliseconds, 1), userId,
                    context.TraceIdentifier, context.Request.ContentLength, context.Response.ContentType);
            }
        });
    }

    public static IEndpointRouteBuilder MapHKShopHealthChecks(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapHealthChecks("/health/live", new HealthCheckOptions { Predicate = static _ => false }).AllowAnonymous();
        endpoints.MapHealthChecks("/health/ready", new HealthCheckOptions { Predicate = static check => check.Tags.Contains("ready") }).AllowAnonymous();
        return endpoints;
    }

    public static bool IsSensitivePath(PathString path) =>
        path.StartsWithSegments("/account") || path.StartsWithSegments("/checkout")
        || path.StartsWithSegments("/cart") || path.StartsWithSegments("/orders")
        || path.StartsWithSegments("/eoffice") || path.StartsWithSegments("/admin")
        || path.StartsWithSegments("/payments") || path.StartsWithSegments("/seller")
        || path.StartsWithSegments("/shipper") || path.StartsWithSegments("/api")
        || path.StartsWithSegments("/internal");
}
