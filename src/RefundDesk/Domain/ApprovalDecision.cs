namespace RefundDesk.Domain;

/// <summary>The human approver's recorded decision (persisted in the task record).</summary>
public static class ApprovalDecision
{
    public const string Approved = "approved";
    public const string Rejected = "rejected";
}
