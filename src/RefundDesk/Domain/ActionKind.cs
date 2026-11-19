namespace RefundDesk.Domain;

/// <summary>
/// The kinds of payment effect an action can request. String constants rather than an
/// enum because the values travel through model prompts, JSON payloads and the
/// persisted ledger as plain strings.
/// </summary>
public static class ActionKind
{
    public const string Refund = "refund";
    public const string CancelBackorder = "cancel_backorder";
}
