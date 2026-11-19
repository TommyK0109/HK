using System.Net.Http.Headers;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Http.Resilience;

namespace HKShop.ServiceDefaults;

public interface IBffAccessTokenStore
{
    ValueTask<string?> GetAccessTokenAsync(HttpContext context, CancellationToken cancellationToken = default);
}

public sealed class PassThroughBffAccessTokenStore : IBffAccessTokenStore
{
    public async ValueTask<string?> GetAccessTokenAsync(HttpContext context, CancellationToken cancellationToken = default)
    {
        if (context.Request.Headers.Authorization.ToString() is { Length: > 7 } auth &&
            auth.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
            return auth["Bearer ".Length..].Trim();

        // Activated when Identity cutover stores access_token in the BFF authentication session.
        if (context.Features.Get<IAuthenticateResultFeature>()?.AuthenticateResult?.Properties is { } props &&
            props.GetTokenValue("access_token") is { Length: > 0 } sessionToken)
            return sessionToken;

        return await ValueTask.FromResult<string?>(null);
    }
}

public sealed class HKShopOutgoingHttpHandler(
    IHttpContextAccessor httpContextAccessor,
    IBffAccessTokenStore tokenStore) : DelegatingHandler
{
    public const string IdempotencyKeyHeader = "Idempotency-Key";
    public const string CorrelationIdHeader = "X-Correlation-Id";

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (IsMutatingMethod(request.Method) && !request.Headers.Contains(IdempotencyKeyHeader))
            request.Headers.TryAddWithoutValidation(IdempotencyKeyHeader, Guid.NewGuid().ToString("D"));

        if (httpContextAccessor.HttpContext is { } httpContext)
        {
            if (!request.Headers.Contains(CorrelationIdHeader) && !string.IsNullOrWhiteSpace(httpContext.TraceIdentifier))
                request.Headers.TryAddWithoutValidation(CorrelationIdHeader, httpContext.TraceIdentifier);

            if (request.Headers.Authorization is null)
            {
                var token = await tokenStore.GetAccessTokenAsync(httpContext, cancellationToken);
                if (!string.IsNullOrWhiteSpace(token))
                    request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            }
        }

        return await base.SendAsync(request, cancellationToken);
    }

    private static bool IsMutatingMethod(HttpMethod method) =>
        method == HttpMethod.Post || method == HttpMethod.Put || method == HttpMethod.Patch || method == HttpMethod.Delete;
}

public static class HKShopHttpResilienceExtensions
{
    public static IServiceCollection AddHKShopHttpResilience(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddHttpContextAccessor();
        services.TryAddSingleton<IBffAccessTokenStore, PassThroughBffAccessTokenStore>();
        services.TryAddTransient<HKShopOutgoingHttpHandler>();
        services.Configure<HttpStandardResilienceOptions>(configuration.GetSection("HttpResilience"));
        return services;
    }

    public static IHttpStandardResiliencePipelineBuilder AddHKShopResilienceHandler(this IHttpClientBuilder builder)
    {
        builder.AddHttpMessageHandler<HKShopOutgoingHttpHandler>();
        return builder.AddStandardResilienceHandler(options =>
        {
            options.Retry.MaxRetryAttempts = 3;
            options.Retry.UseJitter = true;
            options.Retry.Delay = TimeSpan.FromMilliseconds(200);
            options.AttemptTimeout.Timeout = TimeSpan.FromSeconds(5);
            options.TotalRequestTimeout.Timeout = TimeSpan.FromSeconds(15);
            options.CircuitBreaker.SamplingDuration = TimeSpan.FromSeconds(30);
        });
    }
}
