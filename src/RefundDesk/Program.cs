// =====================================================================================
//  Trailhead Gear Refund Desk: one flow that composes the patterns from the talk.
//
//    ① prompt chain with gates  - the whole pipeline below; every arrow is a typed artifact
//    ② router                   - classify, abstain when unsure
//    ⑦ specialist handoff       - typed package, capabilities granted by code
//    ⑤ orchestrator-workers     - the lead decides what research is needed at runtime
//    ③ parallel fan-out         - workers research one source each, at the same time
//    ⑥ evaluator-optimizer      - deterministic checks, then a rubric, with a max round count
//    ⑧ human checkpoint         - a real preview, and the approval binds the executor
//    ④ tool-use loop            - reason / act / observe with budgets and idempotent effects
//    ⑨ durable task + memory    - every stage is saved; stop anywhere and --resume
// =====================================================================================

using System.Diagnostics;
using System.Text;
using Microsoft.Extensions.Configuration;
using RefundDesk;
using RefundDesk.Domain;
using RefundDesk.Infrastructure;
using RefundDesk.Patterns;
using Spectre.Console;

Console.OutputEncoding = Encoding.UTF8;
var cli = Cli.Parse(args);

// --watch: a separate, read-only process that draws the flow from the task record (⑨).
if (cli.Watch)
{
    await FlowView.WatchAsync(Path.Combine(Directory.GetCurrentDirectory(), ".data"), cli.Once);
    return;
}

var config = new ConfigurationBuilder()
    .SetBasePath(AppContext.BaseDirectory)
    .AddJsonFile("appsettings.json", optional: true)
    .AddUserSecrets<Cli>(optional: true)
    .AddEnvironmentVariables("REFUNDDESK_")
    .Build();
var llm = config.GetSection("Llm").Get<LlmSettings>() ?? new LlmSettings();
if (cli.Provider is not null) llm.Provider = cli.Provider;
if (cli.Model is not null) llm.Model = cli.Model;
if (cli.Fast) llm.ScriptedDelayMs = 0;

// Opt-in OpenTelemetry (--otel or OTEL_EXPORTER_OTLP_ENDPOINT). Off by default: no listeners, no cost.
using var telemetry = Telemetry.Start(config.GetSection("Telemetry").Get<TelemetrySettings>() ?? new(), cli.Otel);

var dataDir = Path.Combine(Directory.GetCurrentDirectory(), ".data");
if (cli.Reset && Directory.Exists(dataDir)) Directory.Delete(dataDir, recursive: true);
Directory.CreateDirectory(dataDir);
if (cli.Viz) FlowView.OpenPane();

var meter = new Meter();
var (chat, providerLabel, scripted) = LlmFactory.Create(llm);
var model = new ModelGateway(new MeteredChatClient(Telemetry.Instrument(chat), meter), llm);
// Optional decision model for the router (Microsoft-Decision-1). Not used by the offline replay.
using var decision = scripted ? null : DecisionClient.Create(config.GetSection("Decision").Get<DecisionSettings>() ?? new(), llm, meter);
var store = new TaskStore(dataDir);
var payments = new PaymentGateway(dataDir) { Chaos = cli.Chaos };
var orderBook = new OrderBook(payments);
var outbox = new Outbox(dataDir);
var wall = Stopwatch.StartNew();

TaskRecord task;
if (cli.Resume)
{
    task = store.Load(cli.ResumeId) ?? throw new InvalidOperationException($"No task record found{(cli.ResumeId is null ? "" : $" for {cli.ResumeId}")}.");
}
else
{
    var request = cli.RequestText is null ? SampleData.DefaultRequest : SampleData.DefaultRequest with { Text = cli.RequestText };
    task = store.Create(request);
}
void Save() => store.Save(task);
void Doing(string activity) { task.Activity = activity; Save(); }   // also feeds the --watch flow view
static string ReplyKey(string taskId) => $"{taskId}:reply";

// One trace per run. A resumed run links back to the earlier runs of the same task.
using var root = Telemetry.Source.StartActivity($"refund task {task.Id}", ActivityKind.Internal, default(ActivityContext),
    links: Telemetry.LinksTo(task.Traces));
root?.SetTag("refunddesk.task.id", task.Id);
root?.SetTag("refunddesk.resumed", cli.Resume);
root?.SetTag("refunddesk.chaos", cli.Chaos);
root?.SetTag("refunddesk.provider", providerLabel);
if (Telemetry.ContextOf(root) is { } traceContext) { task.Traces.Add(traceContext); Save(); }

// ---- banner ----------------------------------------------------------------------------
AnsiConsole.Write(new Panel(new Rows(
        new Markup($"[grey]Model:[/] {(scripted ? "[yellow]" : "[green]")}{Ui.E(providerLabel)}[/]" +
                   (decision is null ? "" : $"   [grey]router:[/] [green]{Ui.E(decision.Deployment)}[/] [grey](decision model)[/]")),
        new Markup($"[grey]Task:[/]  {task.Id} {(cli.Resume ? $"[blue](resuming, status: {task.Status})[/]" : "[grey](new)[/]")}" +
                   $"{(cli.Chaos ? "   [red]chaos: payment gateway will time out mid-effect[/]" : "")}"),
        new Markup(Telemetry.IsOn
            ? $"[grey]OTel:[/]  → {Ui.E(Telemetry.Endpoint)}{(Telemetry.CaptureContent ? " [grey](prompts captured)[/]" : "")}  [grey]trace[/] {root?.TraceId.ToHexString()[..7] ?? "-"}"
            : "[grey]OTel:  off (--otel to send traces to the Aspire dashboard)[/]"),
        new Markup("[grey]Flow:[/]  ② route → ⑦ handoff → ⑤ plan → ③ research → ⑥ evaluate → ⑧ approve → ④ execute → verify   [grey](① chain, ⑨ saved at every step)[/]")))
    .Header("[bold] Trailhead Gear · Refund Desk [/]").BorderColor(Color.Yellow));

AnsiConsole.Write(new Panel(Ui.E(string.Join(' ', task.Request.Text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))))
    .Header($"[bold] Incoming message · {Ui.E(task.Request.CustomerName)} ({task.Request.CustomerId}) [/]").BorderColor(Color.Grey));

// ---- ② Router --------------------------------------------------------------------------
Ui.StageHeader("②", "Router", "One cheap classification call. Abstains when unsure; authorization is not the classifier's job.");
using (Telemetry.Source.StartActivity("② route + ⑦ handoff"))
if (!task.Reached("routed"))
{
    Doing("routing");
    (task.Route, task.RoutedBy) = await Ui.Spin("classifying request...", () => new Router(model, decision).ClassifyAsync(task.Request));

    // ⑦ Specialist handoff: deterministic facts + capabilities travel with the task.
    var orderIds = Router.ExtractOrderIds(task.Request.Text);
    task.Orders = orderIds.Select(orderBook.Find).OfType<Order>().Where(o => o.CustomerId == task.Request.CustomerId).ToList();
    task.Handoff = new HandoffPackage(task.Id, "triage-router", $"{task.Route.Category}-specialist",
        task.Route.Rationale, orderIds, Router.CapabilitiesFor(task.Route.Category));
    task.Status = "routed";
    Save();
}
else Ui.Resumed($"route = {task.Route!.Category}");

var routeColor = task.Route!.Category == "unknown" ? "red" : "green";
AnsiConsole.MarkupLine($"  route: [bold {routeColor}]{task.Route.Category}[/]  confidence: {task.Route.Confidence:0.00} " +
                       $"[grey](floor {Router.ConfidenceFloor:0.0})[/]  [grey]{Ui.E(task.Route.Rationale)}[/]");
if (task.RoutedBy is not null) AnsiConsole.MarkupLine($"  [grey]decided by:[/] {Ui.E(task.RoutedBy)}");

if (task.Route.Category != "billing")
{
    var where = task.Route.Category == "unknown" ? "the human support queue (no route fits)" : $"the {task.Route.Category} specialist (not built in this demo)";
    task.Outcome = $"handed off to {where}";
    root?.SetTag("refunddesk.outcome", task.Outcome);
    Doing("done");
    Ui.Warn($"Handed off to {where}. Nothing else runs; tools granted: {(task.Handoff!.Capabilities.Count == 0 ? "none" : string.Join(", ", task.Handoff.Capabilities))}.");
    Ui.Economics(meter, scripted, wall.Elapsed);
    return;
}

// ---- ⑦ Specialist handoff ---------------------------------------------------------------
Ui.StageHeader("⑦", "Specialist handoff", "A typed package moves ownership; capabilities are re-evaluated by code at the boundary.");
var handoff = task.Handoff!;
var handoffTable = new Table().Border(TableBorder.Simple).HideHeaders().AddColumn("").AddColumn("");
handoffTable.AddRow("[grey]owner[/]", $"{handoff.From} → [bold]{handoff.To}[/]");
handoffTable.AddRow("[grey]orders (regex, not a model)[/]", string.Join(", ", handoff.OrderIds.Select(id => $"#{id}")));
handoffTable.AddRow("[grey]facts (order system)[/]", string.Join("\n", task.Orders.Select(o => $"#{o.Id} {Ui.E(o.Item)} · {Ui.Money(o.AmountCents)} · {o.Status}")));
handoffTable.AddRow("[grey]capabilities granted[/]", string.Join(", ", handoff.Capabilities));
AnsiConsole.Write(handoffTable);

// ---- ⑤ Orchestrator + ③ parallel workers -------------------------------------------------
Ui.StageHeader("⑤ + ③", "Orchestrator-workers with parallel fan-out", $"The lead decides what to research at runtime (max {Orchestrator.MaxWorkers} workers); workers run in parallel.");
var orchestrator = new Orchestrator(model);
using (Telemetry.Source.StartActivity("⑤ + ③ research"))
if (!task.Reached("researched"))
{
    Doing("planning research");
    task.Plan = await Ui.Spin("lead is planning research...", () => orchestrator.PlanAsync(task.Request, task.Orders));
    foreach (var s in task.Plan.Subtasks)
        AnsiConsole.MarkupLine($"  [bold]{Ui.E(s.Id)}[/] [blue]{Ui.E(s.Source),-10}[/] {Ui.E(s.Question)}");
    Doing($"researching: {task.Plan.Subtasks.Count} workers in parallel");

    task.Findings = await Ui.Spin($"{task.Plan.Subtasks.Count} workers researching in parallel...", () => orchestrator.ResearchAsync(task.Plan));
    task.Status = "researched";
    Save();
}
else Ui.Resumed($"{task.Findings.Count} findings");

var findingsTable = new Table().Border(TableBorder.Rounded).BorderColor(Color.Grey)
    .AddColumn("Worker").AddColumn("Finding").AddColumn("Evidence (verbatim quote)").AddColumn("Quote in source?");
foreach (var f in task.Findings)
    findingsTable.AddRow($"[blue]{Ui.E(f.Source)}[/]", Ui.E(f.Answer), $"[grey]\"{Ui.E(f.Quote)}\"[/]",
        f.QuoteVerified ? "[green]✓ verified[/]" : "[red]✗ not found[/]");
AnsiConsole.Write(findingsTable);

// ---- ⑥ Evaluator-optimizer --------------------------------------------------------------
Ui.StageHeader("⑥", "Evaluator-optimizer", $"Deterministic checks first, then a model rubric. Revise up to {Evaluator.MaxRounds} rounds, then escalate.");
var evaluator = new Evaluator(model);
using (Telemetry.Source.StartActivity("⑥ evaluate + revise"))
if (!task.Reached("evaluated"))
{
    Evaluation? feedback = null;
    for (var round = 1; round <= Evaluator.MaxRounds; round++)
    {
        using var roundSpan = Telemetry.Source.StartActivity($"round {round}");
        meter.EvaluatorRounds = round;
        Doing($"drafting round {round}");
        var draft = await Ui.Spin(round == 1 ? "lead is drafting a resolution..." : $"lead is revising (round {round})...",
            () => orchestrator.SynthesizeAsync(task.Request, task.Orders, task.Findings, feedback, task.Proposal));
        task.Drafts.Add(draft);
        AnsiConsole.MarkupLine($"  [bold]draft {round}:[/] {Ui.E(draft.Summary)} " +
                               $"[grey]({string.Join(", ", draft.Actions.Select(a => $"{a.Type} #{a.OrderId} {Ui.Money(a.AmountCents)}"))})[/]");

        var verdict = Evaluator.CheckDeterministic(task.Request, task.Orders, task.Findings, draft);
        if (verdict.Pass)
        {
            Ui.Ok("deterministic checks passed (coverage, amounts, ownership, order state, cited evidence)");
            Doing($"evaluating round {round}: rubric");
            verdict = await Ui.Spin("evaluator applying rubric...", () => evaluator.CheckRubricAsync(task.Request, task.Orders, task.Findings, draft));
        }
        task.Evaluations.Add(verdict);
        Save();
        roundSpan?.SetTag("refunddesk.evaluation.kind", verdict.Kind);
        roundSpan?.SetTag("refunddesk.evaluation.pass", verdict.Pass);
        if (!verdict.Pass) roundSpan.Fail($"{verdict.Kind}: {string.Join("; ", verdict.Issues)}");

        if (verdict.Pass) { Ui.Ok($"{verdict.Kind} rubric passed"); break; }
        foreach (var issue in verdict.Issues) Ui.Fail($"{verdict.Kind}: {issue}");
        feedback = verdict;

        // ⑤ adaptive: if the gap is missing evidence, the lead can send workers back out.
        if (round < Evaluator.MaxRounds)
        {
            Doing("follow-up: lead deciding");
            var more = await Ui.Spin("lead deciding whether more research would help...",
                () => orchestrator.FollowUpAsync(task.Request, task.Orders, task.Findings, verdict));
            if (more.Subtasks.Count > 0)
            {
                foreach (var s in more.Subtasks)
                    AnsiConsole.MarkupLine($"  [bold]{Ui.E(s.Id)}[/] [blue]{Ui.E(s.Source),-10}[/] {Ui.E(s.Question)} [grey](follow-up)[/]");
                Doing($"follow-up: researching {string.Join(", ", more.Subtasks.Select(s => s.Source))}");
                var extra = await Ui.Spin($"{more.Subtasks.Count} follow-up worker(s) researching...", () => orchestrator.ResearchAsync(more));
                foreach (var f in extra)
                    AnsiConsole.MarkupLine($"  [blue]{Ui.E(f.Source)}[/] {Ui.E(f.Answer)} {(f.QuoteVerified ? "[green]✓ verified[/]" : "[red]✗ quote not found[/]")}");
                task.Findings.AddRange(extra);
                Save();
            }
        }
    }
    task.Status = "evaluated";
    Save();
}
else Ui.Resumed($"{task.Drafts.Count} draft(s), last evaluation {(task.Evaluations.LastOrDefault()?.Pass == true ? "passed" : "failed")}");

// ---- ⑧ Human checkpoint -----------------------------------------------------------------
Ui.StageHeader("⑧", "Human checkpoint", "Part of the architecture, not an exception handler. The approval binds what the executor may do.");
if (task.Approval is { Decision: "rejected" })
{
    Ui.Warn($"Rejected by {task.Approval.Approver} at {task.Approval.At:t}; task is in the supervisor queue.");
    return;
}
using (var checkpointSpan = Telemetry.Source.StartActivity("⑧ human checkpoint"))
if (!task.Reached("approved"))
{
    var unresolved = task.Evaluations.LastOrDefault() is { Pass: false } last ? new List<Evaluation> { last } : [];
    Doing("awaiting approval");
    HumanCheckpoint.ShowPreview(task, unresolved);
    var choice = HumanCheckpoint.Ask(cli.AutoApprove);
    meter.HumanInterventions++;
    checkpointSpan?.SetTag("refunddesk.decision", choice.ToString().ToLowerInvariant());
    root?.SetTag("refunddesk.outcome", choice == HumanCheckpoint.Choice.Pause ? "paused for human" : null);

    if (choice == HumanCheckpoint.Choice.Pause)
    {
        Doing("paused for a human");
        AnsiConsole.MarkupLine($"\n[blue]Paused.[/] Everything so far is in [bold]{Ui.E(store.PathFor(task.Id))}[/]");
        AnsiConsole.MarkupLine($"Resume with: [bold]dotnet run -- --resume {task.Id}[/]");
        return;
    }

    task.Approval = HumanCheckpoint.Record(task, choice, Environment.UserName);
    if (choice == HumanCheckpoint.Choice.Reject)
    {
        task.Outcome = "rejected; sent to supervisor queue";
        Doing("done");
        Ui.Warn("Rejected. Nothing was executed.");
        return;
    }
    task.Status = "approved";
    Save();
}
else Ui.Resumed($"approved by {task.Approval!.Approver} at {task.Approval.At:t}");
foreach (var a in task.Approval!.Actions)
    Ui.Note($"approved: {a.Action.Type} #{a.Action.OrderId} {Ui.Money(a.Action.AmountCents)}  key={a.IdempotencyKey}");

// ---- ④ Tool-use loop --------------------------------------------------------------------
Ui.StageHeader("④", "Tool-use loop", $"Model chooses each step. Budgets: {Executor.MaxIterations} iterations, {Executor.MaxToolCalls} tool calls, {Executor.TimeBudget.TotalMinutes:0} min. Effects are idempotent.");
if (!task.Reached("executed"))
{
    root?.AddEvent(new ActivityEvent("approved actions handed to the tool-use loop"));
    Doing("executing approved actions");
    var tools = new BillingTools(orderBook, payments, outbox, task.Approval, task.Request.CustomerId, ReplyKey(task.Id), meter);
    var stopReason = await new Executor(model, tools, meter).RunAsync(task.Approval, task.Handoff!.Capabilities, task.ExecutionLog, onStep: Save);
    task.Outcome = stopReason;
    root?.SetTag("refunddesk.outcome", stopReason);
    task.Status = "executed";
    Save();
}
else Ui.Resumed($"executed ({task.Outcome})");

// ---- Verify by reading back the system of record -----------------------------------------
Ui.StageHeader("✓", "Verify", "Don't trust the agent's summary: read the payment ledger and outbox back and compare them to the approval.");
Doing("verifying: reading the ledger back");
var verified = true;
foreach (var a in task.Approval.Actions)
{
    var entries = payments.Ledger.Where(e => e.IdempotencyKey == a.IdempotencyKey).ToList();
    if (entries.Count == 1 && entries[0].AmountCents == a.Action.AmountCents)
        Ui.Ok($"#{a.Action.OrderId}: exactly one {a.Action.Type} of {Ui.Money(entries[0].AmountCents)} ({entries[0].RefundId})");
    else
    {
        verified = false;
        Ui.Fail($"#{a.Action.OrderId}: expected one {Ui.Money(a.Action.AmountCents)} effect, found {entries.Count}");
    }
}
var unapproved = payments.Ledger.Where(e => e.IdempotencyKey.StartsWith(task.Id) && task.Approval.Actions.All(a => a.IdempotencyKey != e.IdempotencyKey)).ToList();
if (unapproved.Count == 0) Ui.Ok("no money moved that the approver did not see");
else { verified = false; Ui.Fail($"{unapproved.Count} ledger entries were not approved"); }
var replies = outbox.Sent.Where(m => m.IdempotencyKey == ReplyKey(task.Id)).ToList();
if (replies.Count == 1 && replies[0].Text == task.Approval.CustomerReply)
    Ui.Ok($"customer reply sent exactly once ({replies[0].MessageId}), text as approved");
else
{
    verified = false;
    Ui.Fail($"expected one approved customer reply, found {replies.Count}");
}
task.Verified = verified;
Doing("done");

Ui.Economics(meter, scripted, wall.Elapsed);
AnsiConsole.MarkupLine($"[grey]Task record:[/] {Ui.E(store.PathFor(task.Id))}");

namespace RefundDesk
{
    /// <summary>Command-line switches. See README for the demo script.</summary>
    public sealed record Cli(bool Resume, string? ResumeId, bool Chaos, bool AutoApprove, bool Reset, bool Fast,
        string? RequestText, string? Provider, string? Model, bool Otel, bool Watch, bool Once, bool Viz)
    {
        public static Cli Parse(string[] args)
        {
            string? Value(string name)
            {
                var i = Array.IndexOf(args, name);
                return i >= 0 && i + 1 < args.Length && !args[i + 1].StartsWith("--") ? args[i + 1] : null;
            }
            bool Has(string name) => args.Contains(name);

            return new Cli(Has("--resume"), Value("--resume"), Has("--chaos"), Has("--auto-approve"), Has("--reset"),
                Has("--fast"), Value("--request"), Value("--provider"), Value("--model"), Has("--otel"),
                Has("--watch"), Has("--once"), Has("--viz"));
        }
    }
}
