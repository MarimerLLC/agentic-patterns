namespace RefundDesk.Domain;

/// <summary>
/// The router's categories. String constants rather than an enum because the values are
/// what the classifier (LLM or decision model) answers with, and they are persisted.
/// </summary>
public static class RouteCategory
{
    public const string Billing = "billing";
    public const string Technical = "technical";
    public const string General = "general";
    public const string Unknown = "unknown";

    /// <summary>Categories a support team handles. Anything else is routed as <see cref="Unknown"/>.</summary>
    public static readonly IReadOnlyList<string> Handled = [Billing, Technical, General];
}
