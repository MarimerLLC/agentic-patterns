using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.Extensions.AI;
using RefundDesk.Domain;

namespace RefundDesk.Infrastructure;

/// <summary>
/// OFFLINE REPLAY - NOT A MODEL. Returns canned, plausible responses for each pipeline
/// stage so the demo runs with no network (conference Wi-Fi) and so the orchestration
/// code can be tested. Everything outside this class is identical in live mode.
/// The first synthesized draft deliberately misses order #1057 so the evaluator has
/// something to catch.
/// </summary>
public sealed class ScriptedChatClient(int delayMs) : IChatClient
{
    private int _callId;

    public async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        var list = messages.ToList();
        var stage = Stage.Current;
        if (delayMs > 0)
            await Task.Delay(delayMs / 2 + Random.Shared.Next(delayMs), cancellationToken);

        var message = stage switch
        {
            StageName.Router => Text(Router(Last(list))),
            StageName.OrchestratorPlan => Text(Plan),
            StageName.OrchestratorFollowUp => Text(new ResearchPlan([])),   // the replay's first plan already covered every source
            _ when StageName.WorkerSource(stage) is { } source => Text(Worker(source)),
            StageName.OrchestratorSynthesize => Text(Synthesize(list.Any(m => m.Text.Contains(Patterns.Orchestrator.FeedbackHeading)))),
            StageName.Evaluator => Text(new Evaluation(true, [])),
            StageName.Executor => Executor(list),
            _ => new ChatMessage(ChatRole.Assistant, $"(scripted client has no response for stage '{stage}')"),
        };

        var inputChars = list.Sum(m => m.Text.Length);
        return new ChatResponse(message)
        {
            Usage = new UsageDetails { InputTokenCount = inputChars / 4, OutputTokenCount = Math.Max(20, message.Text.Length / 4) },
        };
    }

    // ----- canned responses -------------------------------------------------------------

    private static RouteDecision Router(string text)
    {
        var t = text.ToLowerInvariant();
        if (new[] { "refund", "money back", "charged", "charge", "cancel" }.Any(t.Contains))
            return new(RouteCategory.Billing, 0.93, "Customer asks for a refund and to avoid a charge on a second order.");
        if (new[] { "how do i", "set up", "setup", "repair", "fix" }.Any(t.Contains))
            return new(RouteCategory.Technical, 0.81, "Customer needs help using or repairing a product.");
        if (new[] { "shipping", "hours", "do you sell", "in stock" }.Any(t.Contains))
            return new(RouteCategory.General, 0.77, "General store question.");
        return new(RouteCategory.Unknown, 0.31, "Request does not match a support category.");
    }

    private static readonly ResearchPlan Plan = new(
    [
        new("r1", "Is a tent whose pole snapped on its first use, delivered 3 weeks ago, covered, and can the customer get a refund?", KnowledgeSourceId.Warranty),
        new("r2", "Can a used tent be returned for a refund within 30 days of delivery?", KnowledgeSourceId.Returns),
        new("r3", "Can an unshipped backordered item be cancelled, and is the charge refunded?", KnowledgeSourceId.Backorders),
        new("r4", "Is a Summit 2 replacement pole set in stock right now?", KnowledgeSourceId.Inventory),
    ]);

    private static WorkerOutput Worker(string source) => source switch
    {
        KnowledgeSourceId.Warranty => new("Yes. A pole breaking during normal use counts as a manufacturing defect, and if the replacement part is out of stock the full price is refunded to the original payment method.",
            "Structural failure during normal use, including a broken tent pole, is treated as a manufacturing defect.", true),
        KnowledgeSourceId.Returns => new("No. The tent has been used, so it is not returnable under the returns policy; defects go through the warranty policy.",
            "Items that have been used are not eligible for return under this policy; see the warranty policy for defects.", false),
        KnowledgeSourceId.Backorders => new("Yes. An unshipped backordered item can be cancelled at any time and the full charge is refunded.",
            "The customer may cancel a backordered item at any time before it ships, and the full charge is refunded to the original payment method within 3-5 business days.", true),
        KnowledgeSourceId.Inventory => new("No. Summit 2 replacement poles are out of stock until January 15, so a replacement cannot be sent now.",
            "Summit 2 replacement pole set: out of stock, expected restock January 15.", true),
        _ => new("The source does not answer this question.", "", false),
    };

    private static Proposal Synthesize(bool revised)
    {
        var tent = new ProposedAction(ActionKind.Refund, "1042", 34900,
            "Pole failure on first use is a covered defect and replacement poles are out of stock, so the warranty allows a full refund.", KnowledgeSourceId.Warranty);
        var stakes = new ProposedAction(ActionKind.CancelBackorder, "1057", 3900,
            "Stakes are backordered and unshipped; the customer may cancel for a full refund.", KnowledgeSourceId.Backorders);

        return revised
            ? new("Refund the defective Summit 2 tent under warranty and cancel the unshipped stakes backorder.",
                [tent, stakes],
                "Hi Jordan, I'm sorry the Summit 2 pole broke on your first night out. Because replacement poles are out of stock, " +
                "we're refunding the full $349.00 for order #1042 under our warranty. We've also cancelled the backordered stakes (#1057) " +
                "and refunded the $39.00 charge. Both refunds go to your Visa ending 4417 within 3-5 business days.",
                ["The customer's account of the pole failure is accurate; no photo was provided.",
                 "The tent does not need to be returned before the refund."])
            : new("Refund the defective Summit 2 tent under warranty.",
                [tent],
                "Hi Jordan, I'm sorry the Summit 2 pole broke on your first night out. Because replacement poles are out of stock, " +
                "we're refunding the full $349.00 for order #1042 to your Visa ending 4417 under our warranty.",
                ["The customer's account of the pole failure is accurate; no photo was provided."]);
    }

    /// <summary>A small state machine that behaves like a careful tool-using model.</summary>
    private ChatMessage Executor(List<ChatMessage> history)
    {
        var first = history.First(m => m.Role == ChatRole.User).Text;
        const string tag = Patterns.Executor.PlanTag;
        var plan = JsonSerializer.Deserialize<List<ApprovedAction>>(Between(first, $"<{tag}>", $"</{tag}>"), Json.Options)!;

        var calls = history.SelectMany(m => m.Contents).OfType<FunctionCallContent>().ToList();
        var results = history.SelectMany(m => m.Contents).OfType<FunctionResultContent>()
            .ToDictionary(r => r.CallId, r => r.Result?.ToString() ?? "");
        string Arg(FunctionCallContent c, string name) =>
            c.Arguments is { } a && a.TryGetValue(name, out var v) ? v?.ToString() ?? "" : "";

        foreach (var step in plan)
        {
            var a = step.Action;
            if (!calls.Any(c => c.Name == ToolName.GetOrder && Arg(c, "orderId") == a.OrderId))
                return Call(ToolName.GetOrder, new() { ["orderId"] = a.OrderId });

            var effectName = a.Type == ActionKind.Refund ? ToolName.IssueRefund : ToolName.CancelBackorder;
            var effectArgs = a.Type == ActionKind.Refund
                ? new Dictionary<string, object?> { ["orderId"] = a.OrderId, ["amountCents"] = a.AmountCents, ["idempotencyKey"] = step.IdempotencyKey }
                : new Dictionary<string, object?> { ["orderId"] = a.OrderId, ["idempotencyKey"] = step.IdempotencyKey };

            var keyed = calls.Select((c, i) => (c, i)).Where(x => Arg(x.c, "idempotencyKey") == step.IdempotencyKey).ToList();
            var effects = keyed.Where(x => x.c.Name == effectName).ToList();
            if (effects.Count == 0)
                return Call(effectName, effectArgs);

            var lastEffect = effects[^1];
            var lastResult = results.GetValueOrDefault(lastEffect.c.CallId, "");
            if (ToolOutcome.Of(lastResult) == ToolOutcome.Timeout)
            {
                var check = keyed.LastOrDefault(x => x.c.Name == ToolName.GetPaymentStatus && x.i > lastEffect.i);
                if (check.c is null)
                    return Call(ToolName.GetPaymentStatus, new() { ["idempotencyKey"] = step.IdempotencyKey });
                if (ToolOutcome.Of(results.GetValueOrDefault(check.c.CallId, "")) != ToolOutcome.Completed)
                    return Call(effectName, effectArgs);   // safe: same key
            }
        }

        if (!calls.Any(c => c.Name == ToolName.SendCustomerMessage))
            return Call(ToolName.SendCustomerMessage, []);

        return new ChatMessage(ChatRole.Assistant,
            $"All {plan.Count} approved actions are complete and the customer reply was sent." +
            (results.Values.Any(r => ToolOutcome.Of(r) == ToolOutcome.Timeout) ? " One refund timed out; its status check showed it had already completed, so it was not repeated." : ""));
    }

    // ----- helpers ----------------------------------------------------------------------

    private ChatMessage Call(string name, Dictionary<string, object?> args) =>
        new(ChatRole.Assistant, [new FunctionCallContent($"call_{Interlocked.Increment(ref _callId)}", name, args)]);

    private static ChatMessage Text<T>(T value) => new(ChatRole.Assistant, JsonSerializer.Serialize(value, Json.Options));

    private static string Last(List<ChatMessage> messages) => messages.LastOrDefault(m => m.Role == ChatRole.User)?.Text ?? "";

    private static string Between(string s, string start, string end)
    {
        var i = s.IndexOf(start, StringComparison.Ordinal);
        var j = s.IndexOf(end, StringComparison.Ordinal);
        return i >= 0 && j > i ? s[(i + start.Length)..j] : "";
    }

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        foreach (var update in (await GetResponseAsync(messages, options, cancellationToken)).ToChatResponseUpdates())
            yield return update;
    }

    private static readonly ChatClientMetadata Metadata = new("scripted-replay", null, "scripted");

    public object? GetService(Type serviceType, object? serviceKey = null) =>
        serviceKey is null && serviceType.IsInstanceOfType(Metadata) ? Metadata
        : serviceKey is null && serviceType.IsInstanceOfType(this) ? this : null;
    public void Dispose() { }
}
