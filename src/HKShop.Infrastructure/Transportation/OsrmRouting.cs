using System.Net.Http.Json;
using System.Text.Json;
using HKShop.Application.Transportation;

namespace HKShop.Infrastructure.Transportation;

public sealed class OsrmRouting(HttpClient client, TransportSettings settings) : ITransportRouting
{
    public async Task<long?> Distance(TransportPoint origin, TransportPoint destination, CancellationToken token = default)
    {
        if (!Uri.TryCreate(settings.RoutingBaseUrl.TrimEnd('/') + "/", UriKind.Absolute, out var root) ||
            root.Scheme is not ("http" or "https") || root.Host.Length == 0)
            return null;
        var coordinates = FormattableString.Invariant(
            $"{origin.Longitude:R},{origin.Latitude:R};{destination.Longitude:R},{destination.Latitude:R}");
        try
        {
            using var response = await client.GetAsync(new Uri(root, $"route/v1/driving/{coordinates}?overview=false&radiuses=250;250"), token);
            if (!response.IsSuccessStatusCode) return null;
            var result = await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: token);
            if (result.ValueKind != JsonValueKind.Object || !result.TryGetProperty("routes", out var routes) ||
                routes.ValueKind != JsonValueKind.Array || routes.GetArrayLength() == 0 || routes[0].ValueKind != JsonValueKind.Object ||
                !routes[0].TryGetProperty("distance", out var distance) || distance.ValueKind != JsonValueKind.Number ||
                !distance.TryGetDouble(out var meters) ||
                !double.IsFinite(meters) || meters < 0 || meters > 40_100_000)
                return null;
            return checked((long)Math.Round(meters, MidpointRounding.AwayFromZero));
        }
        catch (Exception error) when (!token.IsCancellationRequested &&
            error is HttpRequestException or TaskCanceledException or JsonException)
        {
            return null;
        }
    }
}
