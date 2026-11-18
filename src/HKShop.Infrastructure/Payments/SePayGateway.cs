using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using HKShop.Application;
using HKShop.Application.Payments;
using HKShop.Contracts.Ports;
using HKShop.Domain;

namespace HKShop.Infrastructure.Payments;

public sealed class SePaySettings
{
    public string MerchantId { get; set; } = "";
    public string MerchantSecret { get; set; } = "";
    public string IpnSecret { get; set; } = "";
    public bool SimulateRefunds { get; set; }
}

public sealed class SePayGateway(HttpClient client, SePaySettings settings, ShopSettings shop) : IPaymentGateway, ISePayWebhookHandler
{
    private const string CheckoutAction = "https://pay-sandbox.sepay.vn/v1/checkout/init";
    private const string Api = "https://pgapi-sandbox.sepay.vn";

    public bool Available => !string.IsNullOrWhiteSpace(settings.MerchantId)
        && !string.IsNullOrWhiteSpace(settings.MerchantSecret)
        && !string.IsNullOrWhiteSpace(settings.IpnSecret)
        && Uri.TryCreate(shop.PublicOrigin, UriKind.Absolute, out var origin)
        && origin.Scheme == Uri.UriSchemeHttps && string.IsNullOrEmpty(origin.UserInfo)
        && string.IsNullOrEmpty(origin.Query) && string.IsNullOrEmpty(origin.Fragment)
        && origin.AbsolutePath == "/";

    public HostedCheckout BuildCheckout(string invoice, Guid order, decimal amount)
    {
        if (!Available) throw new ShopRuleException("SePay Sandbox not configured.");
        var callback = $"{shop.PublicOrigin.TrimEnd('/')}/payments/sepay/return/{order:D}";
        List<KeyValuePair<string, string>> fields =
        [
            new("order_amount", amount.ToString("0", CultureInfo.InvariantCulture)),
            new("merchant", settings.MerchantId),
            new("currency", "VND"),
            new("operation", "PURCHASE"),
            new("order_description", $"HKShop {invoice}"),
            new("order_invoice_number", invoice),
            new("success_url", callback),
            new("error_url", callback),
            new("cancel_url", callback)
        ];
        var canonical = string.Join(",", fields.Select(pair => $"{pair.Key}={pair.Value}"));
        var mac = HMACSHA256.HashData(Encoding.UTF8.GetBytes(settings.MerchantSecret), Encoding.UTF8.GetBytes(canonical));
        fields.Add(new("signature", Convert.ToBase64String(mac)));
        return new HostedCheckout(CheckoutAction, fields);
    }

    public bool Authenticate(string supplied) => Available && CryptographicOperations.FixedTimeEquals(
        Encoding.UTF8.GetBytes(supplied), Encoding.UTF8.GetBytes(settings.IpnSecret));

    public GatewayOutcome? ParsePayment(JsonElement body)
    {
        if (!Object(body, "order", out var order) || !Object(body, "transaction", out var transaction))
            return null;
        return ParseOutcome(order, transaction);
    }

    public async Task<GatewayOutcome?> Query(string invoice, CancellationToken cancellationToken)
    {
        if (!Available) throw new ShopRuleException("SePay Sandbox not configured.");
        using var request = new HttpRequestMessage(HttpMethod.Get, $"{Api}/v1/order/detail/{Uri.EscapeDataString(invoice)}");
        request.Headers.Authorization = new AuthenticationHeaderValue("Basic",
            Convert.ToBase64String(Encoding.UTF8.GetBytes($"{settings.MerchantId}:{settings.MerchantSecret}")));
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (response.StatusCode == HttpStatusCode.NotFound) return null;
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var body = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        if (!Object(body.RootElement, "data", out var order) || String(order, "order_invoice_number") != invoice)
            return null;
        if (String(order, "order_status") == "CANCELLED" && Amount(order, "order_amount", out var cancelled))
            return new GatewayOutcome(invoice, "", cancelled, String(order, "order_currency"), false);
        if (!order.TryGetProperty("transactions", out var transactions) || transactions.ValueKind != JsonValueKind.Array)
            return null;
        foreach (var transaction in transactions.EnumerateArray())
        {
            var payment = ParseOutcome(order, transaction);
            if (payment is not null) return payment;
        }
        return null;
    }

    private static GatewayOutcome? ParseOutcome(JsonElement order, JsonElement transaction)
    {
        if (String(order, "order_status") != "CAPTURED" || String(transaction, "transaction_status") != "APPROVED"
            || String(transaction, "transaction_type") != "PAYMENT") return null;

        var invoice = String(order, "order_invoice_number");
        var transactionId = String(transaction, "id");
        var currency = String(order, "order_currency");
        if (invoice.Length is 0 or > 34 || transactionId.Length is 0 or > 120 ||
            currency != String(transaction, "transaction_currency") ||
            !Amount(order, "order_amount", out var total) || !Amount(transaction, "transaction_amount", out var amount) ||
            total != amount || amount <= 0 || decimal.Truncate(amount) != amount) return null;
        return new GatewayOutcome(invoice, transactionId, amount, currency, true);
    }

    private static bool Object(JsonElement value, string key, out JsonElement child)
    {
        child = default;
        return value.ValueKind == JsonValueKind.Object && value.TryGetProperty(key, out child)
            && child.ValueKind == JsonValueKind.Object;
    }

    public string ReadString(JsonElement value, string key) => String(value, key);

    public static string String(JsonElement value, string key) =>
        value.ValueKind == JsonValueKind.Object && value.TryGetProperty(key, out var child) &&
        child.ValueKind == JsonValueKind.String ? child.GetString() ?? "" : "";

    private static bool Amount(JsonElement value, string key, out decimal amount) =>
        decimal.TryParse(String(value, key), NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out amount);
}
