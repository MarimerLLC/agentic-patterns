using RefundDesk.Domain;
using RefundDesk.Infrastructure;
using Spectre.Console;

namespace RefundDesk.Patterns;

/// <summary>
/// ⑧ Human checkpoint. Not "Allow agent?" - the approver sees the action, the target
/// account, the evidence, the assumptions, reversibility, alternatives, and what happens
/// next. The decision is recorded in the task record and binds what the executor may do.
/// </summary>
public static class HumanCheckpoint
{
    public enum Choice { Approve, Reject, Pause }

    public static void ShowPreview(TaskRecord task, IReadOnlyList<Evaluation> unresolved)
    {
        var proposal = task.Proposal!;
        var actions = new Table().Border(TableBorder.Rounded).BorderColor(Color.Grey)
            .AddColumn("Action").AddColumn("Target").AddColumn("Account").AddColumn(new TableColumn("Amount").RightAligned())
            .AddColumn("Reversible?").AddColumn("Evidence");

        foreach (var a in proposal.Actions)
        {
            var order = task.Orders.First(o => o.Id == a.OrderId.TrimStart('#'));
            var evidence = task.Findings.FirstOrDefault(f => f.Source == a.PolicySource);
            actions.AddRow(
                $"[bold]{Ui.E(a.Type)}[/]",
                $"#{order.Id} {Ui.E(order.Item)}\n[grey]{order.Status}[/]",
                $"{Ui.E(task.Request.CustomerName)}\n[grey]{Ui.E(order.PaymentMethod)}[/]",
                $"[bold]{Ui.Money(a.AmountCents)}[/]",
                "[yellow]No[/]",
                evidence is null ? "[red]none[/]"
                    : $"[grey]{Ui.E(a.PolicySource)}{(evidence.QuoteVerified ? " ✓" : " ✗")}:[/] \"{Ui.E(evidence.Quote.Length > 80 ? evidence.Quote[..79] + "…" : evidence.Quote)}\"");
        }

        AnsiConsole.Write(new Panel(new Rows(
                new Markup($"[bold]{Ui.E(proposal.Summary)}[/]"),
                new Text(""),
                actions,
                new Markup($"[bold]Total leaving the business:[/] {Ui.Money(proposal.Actions.Sum(a => a.AmountCents))}"),
                new Text(""),
                new Markup("[bold]Assumptions[/] [grey](not verified)[/]"),
                new Markup(string.Join("\n", proposal.Assumptions.Select(a => $"  • {Ui.E(a)}")) is { Length: > 0 } s ? s : "  [grey](none stated)[/]"),
                new Text(""),
                new Markup("[bold]Customer reply that will be sent[/]"),
                new Panel(Ui.E(proposal.CustomerReply)).BorderColor(Color.Grey),
                new Markup("[bold]Alternatives[/]  [grey]replacement pole set when restocked; store credit; escalate to supervisor[/]"),
                new Markup("[bold]After approval[/]  [grey]an agent executes ONLY these actions with idempotency keys, then the results are read back and verified[/]"),
                unresolved.Count > 0
                    ? new Markup($"[red bold]Evaluator did not sign off:[/] {Ui.E(string.Join("; ", unresolved.SelectMany(e => e.Issues)))}")
                    : new Markup("[green]Evaluator: all deterministic checks and rubric passed[/]")))
            .Header($"[bold] Approval needed · task {task.Id} [/]").BorderColor(Color.Yellow).Padding(1, 0));
    }

    public static Choice Ask(bool autoApprove)
    {
        if (autoApprove)
        {
            AnsiConsole.MarkupLine("[grey]  --auto-approve: approving without a prompt (rehearsal mode)[/]");
            return Choice.Approve;
        }

        return AnsiConsole.Prompt(new SelectionPrompt<Choice>()
            .Title("[bold]Your decision?[/]")
            .AddChoices(Choice.Approve, Choice.Reject, Choice.Pause)
            .UseConverter(c => c switch
            {
                Choice.Approve => "Approve - execute these actions",
                Choice.Reject => "Reject - send to a supervisor queue",
                _ => "Pause - save and decide later (resume with --resume)",
            }));
    }

    public static Approval Record(TaskRecord task, Choice choice, string approver)
    {
        var actions = task.Proposal!.Actions
            .Select((a, i) => new ApprovedAction(a, $"{task.Id}:{i + 1}:{a.Type}:{a.OrderId}"))
            .ToList();
        return new Approval(choice == Choice.Approve ? "approved" : "rejected", approver, DateTimeOffset.Now,
            choice == Choice.Approve ? actions : [], task.Proposal.CustomerReply, null);
    }
}
