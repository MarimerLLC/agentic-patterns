namespace RefundDesk.Domain;

/// <summary>
/// Names of the executor's tools. The same names are the capabilities the router grants,
/// so authorization and the tool allowlist can't drift apart.
/// </summary>
public static class ToolName
{
    public const string GetOrder = "get_order";
    public const string IssueRefund = "issue_refund";
    public const string CancelBackorder = "cancel_backorder";
    public const string GetPaymentStatus = "get_payment_status";
    public const string SendCustomerMessage = "send_customer_message";
}
