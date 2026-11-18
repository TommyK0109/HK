using HKShop.Domain;
using HKShop.Domain.Settlement;

namespace HKShop.Application.Settlement;

public sealed record PayoutGatewayRequest(Guid PayoutId, decimal AmountVnd);
public enum PayoutGatewayStatus { Pending, Succeeded, Failed }
public sealed record PayoutGatewayOutcome(string Reference, decimal AmountVnd, PayoutGatewayStatus Status, string Issue = "");

public interface IPayoutGateway
{
    Task<PayoutGatewayOutcome> Submit(PayoutGatewayRequest request, CancellationToken cancellationToken);
    Task<PayoutGatewayOutcome?> Query(Guid payoutId, CancellationToken cancellationToken);
}

public sealed class PayoutSimulationSettings
{
    public bool RejectTransfers { get; set; }
    public bool LoseSubmissionResponse { get; set; }
}

public sealed class SimulatedPayoutGateway(IShopStore store, PayoutSimulationSettings settings, TimeProvider time) : IPayoutGateway
{
    public async Task<PayoutGatewayOutcome> Submit(PayoutGatewayRequest request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var (transfer, created) = await store.Write(async db =>
        {
            var existing = await db.First(db.Query<SimulatedPayoutTransfer>().Where(t => t.PayoutId == request.PayoutId));
            if (existing is not null)
            {
                if (existing.AmountVnd != request.AmountVnd)
                    throw new ShopRuleException("The payout provider request key already has a different amount.");
                return (existing, false);
            }

            var row = new SimulatedPayoutTransfer
            {
                PayoutId = request.PayoutId,
                AmountVnd = request.AmountVnd,
                ProviderReference = $"SIM-{request.PayoutId:N}",
                Succeeded = !settings.RejectTransfers,
                ConfirmAt = time.GetUtcNow().AddSeconds(30)
            };
            db.Add(row);
            return (row, true);
        });
        if (created && settings.LoseSubmissionResponse)
            throw new TimeoutException("The simulated payout provider committed the request and lost its response.");
        return Outcome(transfer);
    }

    public async Task<PayoutGatewayOutcome?> Query(Guid payoutId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var transfer = await store.Read(db => db.First(db.Query<SimulatedPayoutTransfer>().Where(t => t.PayoutId == payoutId)));
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
