using System.Text.RegularExpressions;
using RefundDesk.Domain;
using RefundDesk.Infrastructure;

namespace RefundDesk.Patterns;

/// <summary>
/// ② Router. One cheap classification call picks the path. Controls from the slide:
/// abstention ("unknown" + a confidence floor), a logged rationale, and authorization
/// that does NOT come from the classifier: capabilities are granted by code per route.
/// </summary>
public sealed partial class Router(ModelGateway model)
{
    public const double ConfidenceFloor = 0.6;
    public static readonly string[] Categories = ["billing", "technical", "general"];

    private const string System = """
        You are the triage router for Trailhead Gear customer support.
        Classify the customer's message into exactly one category:
          billing   - refunds, charges, cancellations, payment problems
          technical - product setup, usage, repair how-to
          general   - store hours, shipping times, product questions
          unknown   - anything else, or when you are not sure
        Prefer "unknown" over guessing. Respond with JSON only.
        """;

    public async Task<RouteDecision> ClassifyAsync(CustomerRequest request, CancellationToken ct = default)
    {
        using var _ = Stage.Enter("router");
        var decision = await model.AskAsync<RouteDecision>(System, request.Text, ct);

        // Abstention is enforced in code, not left to the prompt.
        var category = decision.Category.Trim().ToLowerInvariant();
        if (!Categories.Contains(category) || decision.Confidence < ConfidenceFloor)
            return decision with { Category = "unknown" };
        return decision with { Category = category };
    }

    /// <summary>Capabilities per route. Classification picks the route; code picks the powers.</summary>
    public static IReadOnlyList<string> CapabilitiesFor(string category) => category switch
    {
        "billing" => ["get_order", "issue_refund", "cancel_backorder", "get_payment_status", "send_customer_message"],
        "technical" or "general" => ["get_order", "send_customer_message"],
        _ => [],
    };

    /// <summary>Deterministic data lookup: a regex, not a model. "Why is a model needed at this step?"</summary>
    public static List<string> ExtractOrderIds(string text) =>
        OrderIdPattern().Matches(text).Select(m => m.Groups[1].Value).Distinct().ToList();

    [GeneratedRegex(@"#\s?(\d{4})\b")]
    private static partial Regex OrderIdPattern();
}
