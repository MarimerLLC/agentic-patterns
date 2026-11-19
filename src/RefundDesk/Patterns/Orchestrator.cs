using System.Text;
using System.Text.Json;
using RefundDesk.Domain;
using RefundDesk.Infrastructure;

namespace RefundDesk.Patterns;

/// <summary>
/// ⑤ Orchestrator-workers, with ③ parallel fan-out for the workers.
/// The lead decides at runtime WHICH research is needed (that is what makes it an
/// orchestrator rather than a fixed fan-out), the workers run in parallel against one
/// source each, and the lead synthesizes a typed proposal.
/// Controls: bounded fan-out, task contracts (typed subtasks), worker evidence that code
/// verifies (the quote must really be in the source), and provenance on every action.
/// </summary>
public sealed class Orchestrator(ModelGateway model)
{
    public const int MaxWorkers = 4;

    public async Task<ResearchPlan> PlanAsync(CustomerRequest request, IReadOnlyList<Order> orders, CancellationToken ct = default)
    {
        using var _ = Stage.Enter("orchestrator:plan");
        var catalog = string.Join("\n", SampleData.Knowledge.Values.Select(k => $"  {k.Id}: {k.Title}"));

        var system = $"""
            You are the lead billing specialist. Decide what policy research is needed before
            proposing a resolution. Create one subtask per question you need answered; each
            subtask reads exactly ONE knowledge source. Use at most {MaxWorkers} subtasks and
            only the sources that matter. Policies often depend on facts (stock levels, dates,
            order status); research the facts a policy's conditions depend on, too.
            Knowledge sources:
            {catalog}
            Respond with JSON only.
            """;
        var plan = await model.AskAsync<ResearchPlan>(system, Describe(request, orders), ct);

        // Bounded fan-out and valid sources are enforced here, whatever the model said.
        var valid = plan.Subtasks.Where(s => SampleData.Knowledge.ContainsKey(s.Source)).Take(MaxWorkers).ToList();
        return new ResearchPlan(valid);
    }

    public const int MaxFollowUps = 2;

    /// <summary>
    /// The adaptive half of orchestrator-workers: when the evaluator rejects a draft because
    /// evidence is missing, rewriting the draft cannot fix it. The lead may commission more
    /// research (bounded) before revising.
    /// </summary>
    public async Task<ResearchPlan> FollowUpAsync(
        CustomerRequest request, IReadOnlyList<Order> orders, IReadOnlyList<Finding> findings, Evaluation feedback,
        CancellationToken ct = default)
    {
        using var _ = Stage.Enter("orchestrator:followup");
        var catalog = string.Join("\n", SampleData.Knowledge.Values.Select(k => $"  {k.Id}: {k.Title}"));

        var system = $"""
            You are the lead billing specialist. The evaluator rejected your draft resolution.
            If any issue is caused by a fact you have not researched yet (for example a policy
            whose condition depends on stock, dates or order state), list follow-up subtasks,
            each reading exactly ONE knowledge source, at most {MaxFollowUps}. If more research
            would not help, return an empty list. Knowledge sources:
            {catalog}
            Respond with JSON only.
            """;
        var user = new StringBuilder(Describe(request, orders));
        user.AppendLine("\nResearch so far:");
        foreach (var f in findings)
            user.AppendLine($"  [{f.Source}] Q: {f.Question}\n    A: {f.Answer}");
        user.AppendLine("\nEvaluator issues:");
        foreach (var issue in feedback.Issues) user.AppendLine($"  - {issue}");

        var plan = await model.AskAsync<ResearchPlan>(system, user.ToString(), ct);

        // Same bounds as the first plan, plus fresh ids so findings stay traceable.
        var next = findings.Count;
        var valid = plan.Subtasks.Where(s => SampleData.Knowledge.ContainsKey(s.Source)).Take(MaxFollowUps)
            .Select(s => s with { Id = $"r{++next}" }).ToList();
        return new ResearchPlan(valid);
    }

    public async Task<List<Finding>> ResearchAsync(ResearchPlan plan, CancellationToken ct = default)
    {
        // ③ Parallel fan-out: independent workers, structured output, merged by code.
        var work = plan.Subtasks.Select(subtask => RunWorkerAsync(subtask, ct));
        return [.. await Task.WhenAll(work)];
    }

    private async Task<Finding> RunWorkerAsync(Subtask subtask, CancellationToken ct)
    {
        using var _ = Stage.Enter($"worker:{subtask.Source}");
        var source = SampleData.Knowledge[subtask.Source];

        var system = """
            You are a policy research worker. Answer the question using ONLY the source text.
            Copy one supporting sentence verbatim into "quote". If the source does not answer
            the question, say so and set supports=false. Respond with JSON only.
            """;
        var user = $"Question: {subtask.Question}\n\n<source id=\"{source.Id}\">\n{source.Text}\n</source>";
        var output = await model.AskAsync<WorkerOutput>(system, user, ct);

        // Provenance check in code: an unverifiable quote is evidence of nothing.
        var verified = Normalize(source.Text).Contains(Normalize(output.Quote.TrimEnd('.')), StringComparison.OrdinalIgnoreCase)
                       && output.Quote.Length > 10;
        return new Finding(subtask.Id, subtask.Source, subtask.Question, output.Answer, output.Quote, output.Supports, verified);
    }

    public async Task<Proposal> SynthesizeAsync(
        CustomerRequest request, IReadOnlyList<Order> orders, IReadOnlyList<Finding> findings,
        Evaluation? feedback, Proposal? previous, CancellationToken ct = default)
    {
        using var _ = Stage.Enter("orchestrator:synthesize");
        var system = $"""
            You are the lead billing specialist. Using ONLY the verified findings and order
            facts, propose a resolution. Each action must be "{ActionKind.Refund}" (delivered order) or
            "{ActionKind.CancelBackorder}" (unshipped backordered order), cite the knowledge source that
            justifies it, and use the exact order amount in cents. Write a short, friendly
            customer reply that promises nothing beyond the proposed actions. List anything
            you assumed rather than verified. Respond with JSON only.
            """;

        var user = new StringBuilder(Describe(request, orders));
        user.AppendLine("\nFindings from research workers:");
        foreach (var f in findings)
            user.AppendLine($"  [{f.Source}] {(f.QuoteVerified ? "VERIFIED" : "UNVERIFIED")} Q: {f.Question}\n    A: {f.Answer}\n    Evidence: \"{f.Quote}\"");

        if (feedback is not null && previous is not null)
        {
            user.AppendLine("\nEVALUATOR FEEDBACK on your previous draft. Fix every issue:");
            foreach (var issue in feedback.Issues) user.AppendLine($"  - {issue}");
            user.AppendLine($"\nPrevious draft:\n{JsonSerializer.Serialize(previous, Json.Options)}");
        }

        return await model.AskAsync<Proposal>(system, user.ToString(), ct);
    }

    public static string Describe(CustomerRequest request, IReadOnlyList<Order> orders)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Customer {request.CustomerName} ({request.CustomerId}) wrote:\n\"\"\"\n{request.Text.Trim()}\n\"\"\"");
        sb.AppendLine("Order facts (from the order system, authoritative):");
        foreach (var o in orders)
            sb.AppendLine($"  #{o.Id}: {o.Item}, {o.AmountCents} cents, status {o.Status}, ordered {o.OrderedOn:yyyy-MM-dd}" +
                          (o.DeliveredOn is { } d ? $", delivered {d:yyyy-MM-dd}" : ", not shipped") + $", paid with {o.PaymentMethod}");
        sb.AppendLine($"Today is {DateTime.Today:yyyy-MM-dd}.");
        return sb.ToString();
    }

    private static string Normalize(string s) => string.Join(' ', s.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
}
