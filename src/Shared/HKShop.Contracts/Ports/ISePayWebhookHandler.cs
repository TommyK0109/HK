using System.Text.Json;

namespace HKShop.Application.Payments
{
    public sealed record HostedCheckout(string Action, IReadOnlyList<KeyValuePair<string, string>> Fields);
    public sealed record GatewayOutcome(string Invoice, string Transaction, decimal Amount, string Currency, bool Paid);

    public interface IPaymentGateway
    {
        bool Available { get; }
        HostedCheckout BuildCheckout(string invoice, Guid order, decimal amount);
        Task<GatewayOutcome?> Query(string invoice, CancellationToken cancellationToken);
    }
}

namespace HKShop.Contracts.Ports
{
    using HKShop.Application.Payments;

    public interface ISePayWebhookHandler
    {
        bool Authenticate(string supplied);
        GatewayOutcome? ParsePayment(JsonElement body);
        string ReadString(JsonElement value, string key);
    }
}
