using System.Text.Json;
using RefundDesk.Domain;
using RefundDesk.Infrastructure;

namespace RefundDesk.Patterns;

/// <summary>
/// ⑥ Evaluator-optimizer. Deterministic checks run first (they are cheap and cannot be
/// talked out of anything); only a draft that passes them goes to the model rubric.
/// Controls: max iterations, evidence judged separately from prose, escalate when stuck.
/// </summary>
public sealed class Evaluator(ModelGateway model)
{
    public const int MaxRounds = 3;

    public static Evaluation CheckDeterministic(
        CustomerRequest request, IReadOnlyList<Order> orders, IReadOnlyList<Finding> findings, Proposal proposal)
    {
        var issues = new List<string>();
        // Code checks that evidence EXISTS (a quote verified against the cited source). Whether
        // the evidence is sufficient (e.g. a conditional policy whose condition is met) is a
        // judgement across findings, so it belongs to the rubric below.
        var supported = findings.Where(f => f.QuoteVerified).Select(f => f.Source).ToHashSet();

        // Coverage: every order the customer mentioned needs an action.
        foreach (var id in Router.ExtractOrderIds(request.Text))
            if (!proposal.Actions.Any(a => a.OrderId.TrimStart('#') == id))
                issues.Add($"Customer asked about order #{id} but no action addresses it.");

        foreach (var a in proposal.Actions)
        {
            var order = orders.FirstOrDefault(o => o.Id == a.OrderId.TrimStart('#'));
            if (order is null) { issues.Add($"Action targets unknown order #{a.OrderId}."); continue; }
            if (order.CustomerId != request.CustomerId) issues.Add($"Order #{order.Id} belongs to a different customer.");
            if (a.AmountCents <= 0 || a.AmountCents > order.AmountCents)
                issues.Add($"Amount {Ui.Money(a.AmountCents)} on #{order.Id} is outside 0..{Ui.Money(order.AmountCents)}.");
            if (a.Type == "refund" && order.Status != "Delivered") issues.Add($"#{order.Id} is {order.Status}; 'refund' needs Delivered.");
            if (a.Type == "cancel_backorder" && order.Status != "Backordered") issues.Add($"#{order.Id} is {order.Status}; cancel needs Backordered.");
            if (a.Type is not ("refund" or "cancel_backorder")) issues.Add($"Unknown action type '{a.Type}'.");
            if (!supported.Contains(a.PolicySource)) issues.Add($"Action on #{order.Id} cites '{a.PolicySource}', which has no verified evidence.");
        }

        if (proposal.Actions.GroupBy(a => a.OrderId).Any(g => g.Count() > 1)) issues.Add("More than one action on the same order.");
        return new Evaluation(issues.Count == 0, issues, "deterministic");
    }

    public async Task<Evaluation> CheckRubricAsync(
        CustomerRequest request, IReadOnlyList<Order> orders, IReadOnlyList<Finding> findings, Proposal proposal,
        CancellationToken ct = default)
    {
        using var _ = Stage.Enter("evaluator");
        var system = """
            You are a strict QA reviewer for customer-support resolutions. Grade the proposal
            against this rubric and list every concrete issue:
              1. Every request the customer made is addressed by an action or an explanation.
              2. Every action is justified by the policy findings together with the order facts.
                 Order facts (amounts, dates, status, payment method) come from the order system
                 and are authoritative; do not ask the findings to prove them.
              3. The customer reply is accurate, matches the actions exactly and promises nothing extra.
            Things the proposal lists under "assumptions" will be shown to a human approver who
            decides whether to accept them; do not fail the proposal for stating an assumption.
            Judge substance, not writing style. pass=true only if there are no issues.
            Respond with JSON only.
            """;
        var user = $"""
            {Orchestrator.Describe(request, orders)}

            Findings:
            {string.Join("\n", findings.Select(f => $"[{f.Source}] {f.Answer} Evidence: \"{f.Quote}\""))}

            <proposal>
            {JsonSerializer.Serialize(proposal, Json.Options)}
            </proposal>
            """;
        var result = await model.AskAsync<Evaluation>(system, user, ct);
        return result with { Kind = "model" };
    }
}
