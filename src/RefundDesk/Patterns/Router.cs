using System.Text.Json;
using System.Text.RegularExpressions;
using RefundDesk.Domain;
using RefundDesk.Infrastructure;

namespace RefundDesk.Patterns;

/// <summary>
/// ② Router. One cheap classification call picks the path. Controls from the slide:
/// abstention ("unknown" + a confidence floor), a logged rationale, and authorization
/// that does NOT come from the classifier: capabilities are granted by code per route.
/// </summary>
public sealed partial class Router(ModelGateway model, DecisionClient? decision = null)
{
    // Same categories, written as options for a decision model (Microsoft-Decision-1).
    private const string Instructions = "Which Trailhead Gear support team should handle this customer message?";
    private static readonly Dictionary<string, string> Options = new()
    {
        ["billing"] = "Refunds, charges, cancellations, payment problems",
        ["technical"] = "Setting up, using or repairing a product Trailhead Gear sold",
        ["general"] = "Questions about Trailhead Gear itself: store hours, shipping times, the products we sell",
        ["unknown"] = "Anything not about Trailhead Gear's orders, products or store, for example travel advice or general outdoor questions",
    };

    public const double ConfidenceFloor = 0.6;
    public static readonly string[] Categories = ["billing", "technical", "general"];

    private const string System = """
        You are the triage router for Trailhead Gear customer support.
        Classify the customer's message into exactly one category:
          billing   - refunds, charges, cancellations, payment problems
          technical - setting up, using or repairing a product Trailhead Gear sold
          general   - questions about Trailhead Gear itself: store hours, shipping times,
                      the products we sell
          unknown   - anything that is not about Trailhead Gear's orders, products or
                      store (for example travel advice or general outdoor questions),
                      or when you are not sure
        Support handles only those categories. Prefer "unknown" over guessing.
        Respond with JSON only.
        """;

    /// <summary>
    /// Classify with the cheapest model that can make the decision: a decision model when one is
    /// configured (calibrated probabilities, ~1 output token), otherwise an LLM whose
    /// "confidence" is just a number it wrote. Returns the route and which model decided.
    /// </summary>
    public async Task<(RouteDecision Route, string RoutedBy)> ClassifyAsync(CustomerRequest request, CancellationToken ct = default)
    {
        using var _ = Stage.Enter("router");
        RouteDecision route;
        string routedBy;

        if (decision is not null)
        {
            try
            {
                var a = await decision.ChooseAsync("route", Instructions, request.Text, Options, ct);
                var spread = string.Join(" · ", a.Probabilities.OrderByDescending(p => p.Value).Select(p => $"{p.Key} {p.Value:0.00}"));
                route = new RouteDecision(a.Choice, a.Confidence, $"probabilities: {spread}");
                routedBy = $"{decision.Deployment} (decision model, calibrated)";
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
            {
                // The cheap path failing must not stop the task: fall back to the LLM, visibly.
                Ui.Warn($"{decision.Deployment} unavailable ({ex.Message.Split('\n')[0]}); falling back to the LLM router.");
                route = await model.AskAsync<RouteDecision>(System, request.Text, ct);
                routedBy = "LLM (fallback; self-reported confidence)";
            }
        }
        else
        {
            route = await model.AskAsync<RouteDecision>(System, request.Text, ct);
            routedBy = "LLM (self-reported confidence)";
        }

        // Abstention is enforced in code, not left to the prompt.
        var category = route.Category.Trim().ToLowerInvariant();
        if (!Categories.Contains(category) || route.Confidence < ConfidenceFloor)
            return (route with { Category = "unknown" }, routedBy);
        return (route with { Category = category }, routedBy);
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
