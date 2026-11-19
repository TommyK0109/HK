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
using HKShop.Domain.Orders;
using HKShop.Domain.Settlement;
using HKShop.Payments.Api.Domain;
using HKShop.Payments.Api.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace HKShop.Payments.Api.Application;

public sealed record RefundGatewayRequest(string RequestKey, string? CaptureReference, decimal AmountVnd);
public enum RefundGatewayStatus { Pending, Succeeded, NeedsReview }
public sealed record RefundGatewayOutcome(
    RefundGatewayStatus Status,
    string Reference,
    decimal AmountVnd,
    string Issue = "",
    bool Simulated = false);

public sealed record PayoutGatewayRequest(Guid PayoutId, decimal AmountVnd);
public enum PayoutGatewayStatus { Pending, Succeeded, Failed }
public sealed record PayoutGatewayOutcome(
    string Reference,
    decimal AmountVnd,
    PayoutGatewayStatus Status,
    string Issue = "");

public sealed class SePayOptions
{
    public string MerchantId { get; set; } = "";
    public string MerchantSecret { get; set; } = "";
    public string IpnSecret { get; set; } = "";
    public bool SimulateRefunds { get; set; }
}

public sealed class PayoutSimulationOptions
{
    public bool RejectTransfers { get; set; }
    public bool LoseSubmissionResponse { get; set; }
}

public sealed class SePayPaymentGateway(HttpClient client, SePayOptions settings, ShopSettings shop)
    : IPaymentGateway, ISePayWebhookHandler
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
        if (!Object(body.RootElement, "data", out var order) || ReadString(order, "order_invoice_number") != invoice)
            return null;
        if (ReadString(order, "order_status") == "CANCELLED" && Amount(order, "order_amount", out var cancelled))
            return new GatewayOutcome(invoice, "", cancelled, ReadString(order, "order_currency"), false);
        if (!order.TryGetProperty("transactions", out var transactions) || transactions.ValueKind != JsonValueKind.Array)
            return null;
        foreach (var transaction in transactions.EnumerateArray())
        {
            var payment = ParseOutcome(order, transaction);
            if (payment is not null) return payment;
        }
        return null;
    }

    public string ReadString(JsonElement value, string key) =>
        value.ValueKind == JsonValueKind.Object && value.TryGetProperty(key, out var child) &&
        child.ValueKind == JsonValueKind.String ? child.GetString() ?? "" : "";

    private GatewayOutcome? ParseOutcome(JsonElement order, JsonElement transaction)
    {
        if (ReadString(order, "order_status") != "CAPTURED" ||
            ReadString(transaction, "transaction_status") != "APPROVED" ||
            ReadString(transaction, "transaction_type") != "PAYMENT") return null;

        var invoice = ReadString(order, "order_invoice_number");
        var transactionId = ReadString(transaction, "id");
        var currency = ReadString(order, "order_currency");
        if (invoice.Length is 0 or > 34 || transactionId.Length is 0 or > 120 ||
            currency != ReadString(transaction, "transaction_currency") ||
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

    private bool Amount(JsonElement value, string key, out decimal amount) =>
        decimal.TryParse(ReadString(value, key), NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out amount);
}

public sealed class SePayRefundGatewayService(SePayOptions settings)
{
    public RefundGatewayOutcome Submit(RefundGatewayRequest request, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (!settings.SimulateRefunds)
            return new RefundGatewayOutcome(RefundGatewayStatus.NeedsReview, "", request.AmountVnd,
                "Use the request key to track the external refund. Record the reference after verifying payment to the buyer.");

        return new RefundGatewayOutcome(RefundGatewayStatus.Pending, SimulatedReference(request),
            request.AmountVnd, "Sandbox simulation pending confirmation.");
    }

    public RefundGatewayOutcome Query(RefundGatewayRequest request, string reference, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var simulated = SimulatedReference(request);
        return reference == simulated
            ? new RefundGatewayOutcome(RefundGatewayStatus.Succeeded, reference, request.AmountVnd, Simulated: true)
            : new RefundGatewayOutcome(RefundGatewayStatus.NeedsReview, reference, request.AmountVnd,
                "Verify the refund result externally and record the confirmed reference.");
    }

    private static string SimulatedReference(RefundGatewayRequest request) =>
        $"SIM-{Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(request.RequestKey)))}";
}

public sealed class SimulatedPayoutGatewayService(
    IDbContextFactory<PaymentsDbContext> factory,
    PayoutSimulationOptions settings,
    TimeProvider time)
{
    public async Task<PayoutGatewayOutcome> SubmitAsync(PayoutGatewayRequest request, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        SimulatedPayoutTransfer transfer;
        var created = false;
        await using (var db = await factory.CreateDbContextAsync(ct))
        {
            var existing = await db.SimulatedPayoutTransfers.FirstOrDefaultAsync(t => t.PayoutId == request.PayoutId, ct);
            if (existing is not null)
            {
                if (existing.AmountVnd != request.AmountVnd)
                    throw new ShopRuleException("The payout provider request key already has a different amount.");
                transfer = existing;
            }
            else
            {
                transfer = new SimulatedPayoutTransfer
                {
                    PayoutId = request.PayoutId,
                    AmountVnd = request.AmountVnd,
                    ProviderReference = $"SIM-{request.PayoutId:N}",
                    Succeeded = !settings.RejectTransfers,
                    ConfirmAt = time.GetUtcNow().AddSeconds(30)
                };
                db.SimulatedPayoutTransfers.Add(transfer);
                await db.SaveChangesAsync(ct);
                created = true;
            }
        }
        if (created && settings.LoseSubmissionResponse)
            throw new TimeoutException("The simulated payout provider committed the request and lost its response.");
        return Outcome(transfer);
    }

    public async Task<PayoutGatewayOutcome?> QueryAsync(Guid payoutId, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        await using var db = await factory.CreateDbContextAsync(ct);
        var transfer = await db.SimulatedPayoutTransfers.AsNoTracking().FirstOrDefaultAsync(t => t.PayoutId == payoutId, ct);
        return transfer is null ? null : Outcome(transfer);
    }

    private PayoutGatewayOutcome Outcome(SimulatedPayoutTransfer transfer)
    {
        if (time.GetUtcNow() < transfer.ConfirmAt)
            return new PayoutGatewayOutcome(transfer.ProviderReference, transfer.AmountVnd,
                PayoutGatewayStatus.Pending, "Waiting for the simulated payout confirmation.");
        return transfer.Succeeded
            ? new PayoutGatewayOutcome(transfer.ProviderReference, transfer.AmountVnd, PayoutGatewayStatus.Succeeded)
            : new PayoutGatewayOutcome(transfer.ProviderReference, transfer.AmountVnd,
                PayoutGatewayStatus.Failed, "The simulated payout provider rejected the transfer.");
    }
}
