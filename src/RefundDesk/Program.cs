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

var dataDir = Path.Combine(Directory.GetCurrentDirectory(), ".data");
if (cli.Reset && Directory.Exists(dataDir)) Directory.Delete(dataDir, recursive: true);
Directory.CreateDirectory(dataDir);

var meter = new Meter();
var (chat, providerLabel, scripted) = LlmFactory.Create(llm);
var model = new ModelGateway(new MeteredChatClient(chat, meter), llm);
var store = new TaskStore(dataDir);
var payments = new PaymentGateway(dataDir) { Chaos = cli.Chaos };
var orderBook = new OrderBook(payments);
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

// ---- banner ----------------------------------------------------------------------------
AnsiConsole.Write(new Panel(new Rows(
        new Markup($"[grey]Model:[/] {(scripted ? "[yellow]" : "[green]")}{Ui.E(providerLabel)}[/]"),
        new Markup($"[grey]Task:[/]  {task.Id} {(cli.Resume ? $"[blue](resuming, status: {task.Status})[/]" : "[grey](new)[/]")}" +
                   $"{(cli.Chaos ? "   [red]chaos: payment gateway will time out mid-effect[/]" : "")}"),
        new Markup("[grey]Flow:[/]  ② route → ⑦ handoff → ⑤ plan → ③ research → ⑥ evaluate → ⑧ approve → ④ execute → verify   [grey](① chain, ⑨ saved at every step)[/]")))
    .Header("[bold] Trailhead Gear · Refund Desk [/]").BorderColor(Color.Yellow));

AnsiConsole.Write(new Panel(Ui.E(string.Join(' ', task.Request.Text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))))
    .Header($"[bold] Incoming message · {Ui.E(task.Request.CustomerName)} ({task.Request.CustomerId}) [/]").BorderColor(Color.Grey));

// ---- ② Router --------------------------------------------------------------------------
Ui.StageHeader("②", "Router", "One cheap classification call. Abstains when unsure; authorization is not the classifier's job.");
if (!task.Reached("routed"))
{
    task.Route = await Ui.Spin("classifying request...", () => new Router(model).ClassifyAsync(task.Request));

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

if (task.Route.Category != "billing")
{
    var where = task.Route.Category == "unknown" ? "the human support queue (no route fits)" : $"the {task.Route.Category} specialist (not built in this demo)";
    task.Outcome = $"handed off to {where}";
    Save();
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
if (!task.Reached("researched"))
{
    task.Plan = await Ui.Spin("lead is planning research...", () => orchestrator.PlanAsync(task.Request, task.Orders));
    foreach (var s in task.Plan.Subtasks)
        AnsiConsole.MarkupLine($"  [bold]{Ui.E(s.Id)}[/] [blue]{Ui.E(s.Source),-10}[/] {Ui.E(s.Question)}");

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
if (!task.Reached("evaluated"))
{
    Evaluation? feedback = null;
    for (var round = 1; round <= Evaluator.MaxRounds; round++)
    {
        meter.EvaluatorRounds = round;
        var draft = await Ui.Spin(round == 1 ? "lead is drafting a resolution..." : $"lead is revising (round {round})...",
            () => orchestrator.SynthesizeAsync(task.Request, task.Orders, task.Findings, feedback, task.Proposal));
        task.Drafts.Add(draft);
        AnsiConsole.MarkupLine($"  [bold]draft {round}:[/] {Ui.E(draft.Summary)} " +
                               $"[grey]({string.Join(", ", draft.Actions.Select(a => $"{a.Type} #{a.OrderId} {Ui.Money(a.AmountCents)}"))})[/]");

        var verdict = Evaluator.CheckDeterministic(task.Request, task.Orders, task.Findings, draft);
        if (verdict.Pass)
        {
            Ui.Ok("deterministic checks passed (coverage, amounts, ownership, order state, cited evidence)");
            verdict = await Ui.Spin("evaluator applying rubric...", () => evaluator.CheckRubricAsync(task.Request, task.Findings, draft));
        }
        task.Evaluations.Add(verdict);
        Save();

        if (verdict.Pass) { Ui.Ok($"{verdict.Kind} rubric passed"); break; }
        foreach (var issue in verdict.Issues) Ui.Fail($"{verdict.Kind}: {issue}");
        feedback = verdict;
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
if (!task.Reached("approved"))
{
    var unresolved = task.Evaluations.LastOrDefault() is { Pass: false } last ? new List<Evaluation> { last } : [];
    HumanCheckpoint.ShowPreview(task, unresolved);
    var choice = HumanCheckpoint.Ask(cli.AutoApprove);
    meter.HumanInterventions++;

    if (choice == HumanCheckpoint.Choice.Pause)
    {
        Save();
        AnsiConsole.MarkupLine($"\n[blue]Paused.[/] Everything so far is in [bold]{Ui.E(store.PathFor(task.Id))}[/]");
        AnsiConsole.MarkupLine($"Resume with: [bold]dotnet run -- --resume {task.Id}[/]");
        return;
    }

    task.Approval = HumanCheckpoint.Record(task, choice, Environment.UserName);
    if (choice == HumanCheckpoint.Choice.Reject)
    {
        task.Outcome = "rejected; sent to supervisor queue";
        Save();
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
    var tools = new BillingTools(orderBook, payments, task.Approval, meter);
    var stopReason = await new Executor(model, tools, meter).RunAsync(task.Approval, task.Handoff!.Capabilities, task.ExecutionLog);
    task.Outcome = stopReason;
    task.Status = "executed";
    Save();
}
else Ui.Resumed($"executed ({task.Outcome})");

// ---- Verify by reading back the system of record -----------------------------------------
Ui.StageHeader("✓", "Verify", "Don't trust the agent's summary: read the payment ledger back and compare it to the approval.");
foreach (var a in task.Approval.Actions)
{
    var entries = payments.Ledger.Where(e => e.IdempotencyKey == a.IdempotencyKey).ToList();
    if (entries.Count == 1 && entries[0].AmountCents == a.Action.AmountCents)
        Ui.Ok($"#{a.Action.OrderId}: exactly one {a.Action.Type} of {Ui.Money(entries[0].AmountCents)} ({entries[0].RefundId})");
    else
        Ui.Fail($"#{a.Action.OrderId}: expected one {Ui.Money(a.Action.AmountCents)} effect, found {entries.Count}");
}
var unapproved = payments.Ledger.Where(e => e.IdempotencyKey.StartsWith(task.Id) && task.Approval.Actions.All(a => a.IdempotencyKey != e.IdempotencyKey)).ToList();
if (unapproved.Count == 0) Ui.Ok("no money moved that the approver did not see");
else Ui.Fail($"{unapproved.Count} ledger entries were not approved");

Ui.Economics(meter, scripted, wall.Elapsed);
AnsiConsole.MarkupLine($"[grey]Task record:[/] {Ui.E(store.PathFor(task.Id))}");

namespace RefundDesk
{
    /// <summary>Command-line switches. See README for the demo script.</summary>
    public sealed record Cli(bool Resume, string? ResumeId, bool Chaos, bool AutoApprove, bool Reset, bool Fast,
        string? RequestText, string? Provider, string? Model)
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
                Has("--fast"), Value("--request"), Value("--provider"), Value("--model"));
        }
    }
}
