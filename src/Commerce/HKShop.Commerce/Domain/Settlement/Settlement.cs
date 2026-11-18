namespace HKShop.Domain.Settlement;

public sealed class LedgerAccount
{
    public long Id { get; set; }
    public long? SellerId { get; set; }
    public LedgerAccountType Type { get; set; }
}

public sealed class LedgerJournal
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid? SellerOrderId { get; set; }
    public Guid? PaymentAttemptId { get; set; }
    public Guid? PayoutId { get; set; }
    public long? RefundId { get; set; }
    public string BusinessKey { get; set; } = "";
    public LedgerEventType Type { get; set; }
    public DateTimeOffset PostedAt { get; set; }
    public List<LedgerEntry> Entries { get; set; } = [];
}

public sealed class LedgerEntry
{
    public long Id { get; set; }
    public Guid JournalId { get; set; }
    public long AccountId { get; set; }
    public decimal AmountVnd { get; set; }
    public LedgerSide Side { get; set; }
}

public sealed class Payout
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public long SellerId { get; set; }
    public Guid SubmissionKey { get; set; }
    public decimal AmountVnd { get; set; }
    public PayoutStatus Status { get; set; }
    public string ProviderReference { get; set; } = "";
    public string Issue { get; set; } = "";
    public string ReviewNote { get; set; } = "";
    public string RequestedBy { get; set; } = "";
    public DateTimeOffset RequestedAt { get; set; }
    public DateTimeOffset? ApprovedAt { get; set; }
    public DateTimeOffset? NextAttemptAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
    public int Attempts { get; set; }
}

public sealed class SimulatedPayoutTransfer
{
    public Guid PayoutId { get; set; }
    public string ProviderReference { get; set; } = "";
    public decimal AmountVnd { get; set; }
    public bool Succeeded { get; set; }
    public DateTimeOffset ConfirmAt { get; set; }
}
