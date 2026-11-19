using System.ComponentModel;

namespace RefundDesk.Domain;

// ---------------------------------------------------------------------------
// Typed artifacts passed between stages. Every hand-off in the pipeline is one
// of these records, never free text: that is what makes each gate checkable.
// ---------------------------------------------------------------------------

public record CustomerRequest(string CustomerId, string CustomerName, string Text);

public record Order(
    string Id,
    string CustomerId,
    string Item,
    int AmountCents,
    string Status,              // Delivered | Backordered | Refunded | Cancelled
    DateOnly OrderedOn,
    DateOnly? DeliveredOn,
    string PaymentMethod);

// ② Router output
public record RouteDecision(
    [property: Description("One of: billing, technical, general, unknown")] string Category,
    [property: Description("0.0 to 1.0: how sure you are of the category")] double Confidence,
    [property: Description("One sentence explaining the choice")] string Rationale);

// ⑦ Specialist handoff: what travels with the task when ownership changes
public record HandoffPackage(
    string TaskId,
    string From,
    string To,
    string Summary,
    IReadOnlyList<string> OrderIds,
    IReadOnlyList<string> Capabilities);

// ⑤ Orchestrator plan
public record Subtask(
    [property: Description("Short id such as r1, r2")] string Id,
    [property: Description("The specific question this worker must answer")] string Question,
    [property: Description("Exactly one knowledge source id from the catalog")] string Source);

public record ResearchPlan(List<Subtask> Subtasks);

// ③ Worker output (model) and the verified version (code)
public record WorkerOutput(
    [property: Description("Direct answer to the question, one or two sentences")] string Answer,
    [property: Description("Verbatim sentence copied from the source that supports the answer")] string Quote,
    [property: Description("True if the source supports giving the customer what they asked for")] bool Supports);

public record Finding(string SubtaskId, string Source, string Question, string Answer, string Quote, bool Supports, bool QuoteVerified);

// ⑤ Lead synthesis
public record ProposedAction(
    [property: Description($"{ActionKind.Refund} (delivered order) or {ActionKind.CancelBackorder} (unshipped backordered order)")] string Type,
    [property: Description("Order id without '#'")] string OrderId,
    [property: Description("Amount in cents")] int AmountCents,
    [property: Description("Why this action is justified")] string Reason,
    [property: Description("Knowledge source id whose finding justifies this action")] string PolicySource);

public record Proposal(
    string Summary,
    List<ProposedAction> Actions,
    [property: Description("The message that will be sent to the customer")] string CustomerReply,
    [property: Description("Anything assumed rather than verified")] List<string> Assumptions);

// ⑥ Evaluator output
public record Evaluation(bool Pass, List<string> Issues, string Kind = "model");

// ⑧ Human decision
public record ApprovedAction(ProposedAction Action, string IdempotencyKey);

public record Approval(
    string Decision,            // approved | rejected
    string Approver,
    DateTimeOffset At,
    List<ApprovedAction> Actions,
    string CustomerReply,
    string? Note);
