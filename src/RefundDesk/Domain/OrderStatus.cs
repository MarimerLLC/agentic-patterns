namespace RefundDesk.Domain;

/// <summary>
/// Order states in the order system of record. String constants rather than an enum
/// because the values travel through model prompts and the persisted task record.
/// </summary>
public static class OrderStatus
{
    public const string Delivered = "Delivered";
    public const string Backordered = "Backordered";
    public const string Refunded = "Refunded";
    public const string Cancelled = "Cancelled";
}
