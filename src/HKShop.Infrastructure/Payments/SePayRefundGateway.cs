using System.Security.Cryptography;
using System.Text;
using HKShop.Application.Payments;

namespace HKShop.Infrastructure.Payments;

public sealed class SePayRefundGateway(SePaySettings settings) : IRefundGateway
{
    public Task<RefundGatewayOutcome> Submit(RefundGatewayRequest request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!settings.SimulateRefunds)
            return Task.FromResult(new RefundGatewayOutcome(RefundGatewayStatus.NeedsReview, "", request.AmountVnd,
                "Use the request key to track the external refund. Record the reference after verifying payment to the buyer."));

        return Task.FromResult(new RefundGatewayOutcome(RefundGatewayStatus.Pending, SimulatedReference(request),
            request.AmountVnd, "Sandbox simulation pending confirmation."));
    }

    public Task<RefundGatewayOutcome> Query(RefundGatewayRequest request, string reference, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var simulated = SimulatedReference(request);
        return Task.FromResult(reference == simulated
            ? new RefundGatewayOutcome(RefundGatewayStatus.Succeeded, reference, request.AmountVnd, Simulated: true)
            : new RefundGatewayOutcome(RefundGatewayStatus.NeedsReview, reference, request.AmountVnd,
                "Verify the refund result externally and record the confirmed reference."));
    }

    private static string SimulatedReference(RefundGatewayRequest request) =>
        $"SIM-{Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(request.RequestKey)))}";
}
