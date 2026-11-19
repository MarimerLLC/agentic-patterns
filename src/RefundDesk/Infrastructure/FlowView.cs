using System.Diagnostics;
using System.Text.Json;
using Spectre.Console;
using Spectre.Console.Rendering;

namespace RefundDesk.Infrastructure;

/// <summary>
/// Live picture of the composed flow, drawn by a SEPARATE process (--watch) that only reads
/// the durable task record. It never talks to the running agent: the record (⑨) is the
/// single source of truth, the same file --resume reads. Meant for a terminal pane on the
/// right; --viz opens one in Windows Terminal.
/// </summary>
public static class FlowView
{
    private static readonly string[] Spinner = ["◐", "◓", "◑", "◒"];

    public static async Task WatchAsync(string dataDir, bool once)
    {
        Console.Title = "Refund Desk · flow";
        TaskRecord? last = null;
        DateTime lastWrite = default;
        string? lastPath = null;

        if (once)
        {
            AnsiConsole.Write(Render(Read(dataDir, ref last, ref lastWrite, ref lastPath), lastPath, lastWrite, 0));
            return;
        }

        if (Console.IsOutputRedirected)
        {
            // No console to redraw in place (piped/logged): print a snapshot whenever the record changes.
            var seen = (Path: (string?)null, Write: default(DateTime));
            while (true)
            {
                var task = Read(dataDir, ref last, ref lastWrite, ref lastPath);
                if ((lastPath, lastWrite) != seen)
                {
                    seen = (lastPath, lastWrite);
                    AnsiConsole.Write(Render(task, lastPath, lastWrite, 0));
                }
                await Task.Delay(250);
            }
        }

        var frame = 0;
        await AnsiConsole.Live(Render(null, null, default, 0)).AutoClear(false).Overflow(VerticalOverflow.Crop)
            .StartAsync(async ctx =>
            {
                while (true)
                {
                    var task = Read(dataDir, ref last, ref lastWrite, ref lastPath);
                    ctx.UpdateTarget(Render(task, lastPath, lastWrite, frame++));
                    await Task.Delay(250);
                }
            });
    }

    /// <summary>Newest task record; keeps the last good copy while a save or --reset is in flight.</summary>
    private static TaskRecord? Read(string dataDir, ref TaskRecord? last, ref DateTime lastWrite, ref string? lastPath)
    {
        try
        {
            var dir = new DirectoryInfo(Path.Combine(dataDir, "tasks"));
            if (!dir.Exists) return last = null;
            var file = dir.GetFiles("T-*.json").OrderByDescending(f => f.LastWriteTimeUtc).FirstOrDefault();
            if (file is null) return last = null;
            if (file.FullName == lastPath && file.LastWriteTimeUtc == lastWrite) return last;

            var record = JsonSerializer.Deserialize<TaskRecord>(File.ReadAllText(file.FullName), Json.Options);
            (last, lastWrite, lastPath) = (record, file.LastWriteTimeUtc, file.FullName);
            return record;
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return last;   // mid-save or mid-reset: show the previous state
        }
    }

    private enum S { Pending, Active, Done, Failed, Waiting, Skipped }

    public static IRenderable Render(TaskRecord? t, string? path, DateTime savedUtc, int frame)
    {
        if (t is null)
            return Frame(new Markup("[grey]Waiting for a task...\n\nStart one in the other pane:\n  dotnet run -- --reset --chaos[/]"), "no task yet");

        var spin = Spinner[frame % Spinner.Length];
        var act = t.Activity ?? "";
        var grid = new Grid().AddColumn(new GridColumn().NoWrap().Width(18)).AddColumn();
        var offRoute = t.Route is not null && t.Route.Category != "billing";

        void Stage(S s, string name, string detail = "")
        {
            var (icon, color) = s switch
            {
                S.Done => ("●", "green"),
                S.Active => (spin, "yellow"),
                S.Failed => ("✗", "red"),
                S.Waiting => ("⏸", "yellow"),
                S.Skipped => ("·", "grey"),
                _ => ("○", "grey"),
            };
            var nameColor = s is S.Pending or S.Skipped ? "grey" : "bold";
            grid.AddRow(new Markup($"[{color}]{icon}[/] [{nameColor}]{Ui.E(name)}[/]"), new Markup(detail));
        }
        void Sub(string text, string detail) => grid.AddRow(new Markup($"  [grey]{text}[/]"), new Markup(detail));
        void Link() => grid.AddRow(new Markup("[grey]│[/]"), new Text(""));
        S Is(bool done, params string[] activities) =>
            done ? S.Done : activities.Any(a => act.StartsWith(a, StringComparison.OrdinalIgnoreCase)) ? S.Active : offRoute ? S.Skipped : S.Pending;

        // ② Router
        var routeDetail = t.Route is null ? "" :
            $"[{(offRoute ? "red" : "green")}]{Ui.E(t.Route.Category)}[/] {t.Route.Confidence:0.00} [grey]{Ui.E(Short(t.RoutedBy?.Split(' ')[0] ?? "", 14))}[/]";
        Stage(Is(t.Route is not null, "routing"), "② Router", routeDetail);
        Link();

        // ⑦ Handoff
        Stage(offRoute ? S.Done : Is(t.Handoff is not null && t.Route is not null, "routing"), "⑦ Handoff",
            t.Handoff is null ? "" : offRoute
                ? $"[yellow]→ {(t.Route!.Category == "unknown" ? "human queue" : $"{t.Route.Category} specialist")}[/]"
                : $"{Ui.E(string.Join(" ", t.Handoff.OrderIds.Select(id => $"#{id}")))} [grey]→ {Ui.E(t.Handoff.To)}[/]");
        Link();

        // ⑤ Orchestrator + ③ workers
        Stage(Is(t.Plan is not null, "planning"), "⑤ Orchestrator", t.Plan is null ? "" : $"{t.Plan.Subtasks.Count} research tasks");
        if (t.Plan is not null)
        {
            var researching = act.StartsWith("research", StringComparison.OrdinalIgnoreCase);
            foreach (var s in t.Plan.Subtasks)
            {
                var f = t.Findings.FirstOrDefault(x => x.SubtaskId == s.Id);
                Sub($"├ ③ {Short(s.Source, 11)}", f is null
                    ? researching ? $"[yellow]{spin} running[/]" : "[grey]○[/]"
                    : f.QuoteVerified ? "[green]✓ quote verified[/]" : "[red]✗ quote not found[/]");
            }
            foreach (var f in t.Findings.Where(x => t.Plan.Subtasks.All(s => s.Id != x.SubtaskId)))
                Sub($"├ ③ {Short(f.Source, 11)}", $"{(f.QuoteVerified ? "[green]✓[/]" : "[red]✗[/]")} [blue]follow-up[/]");
            if (act.StartsWith("follow-up", StringComparison.OrdinalIgnoreCase))
                Sub("├ ③ follow-up", $"[yellow]{spin} {Ui.E(Short(act["follow-up".Length..].Trim(' ', ':'), 22))}[/]");
        }
        Link();

        // ⑥ Evaluator
        var evalDone = t.Reached("evaluated");
        var lastEval = t.Evaluations.LastOrDefault();
        Stage(evalDone && lastEval is { Pass: false } ? S.Failed : Is(evalDone, "drafting", "evaluating", "follow-up"),
            "⑥ Evaluator", t.Evaluations.Count == 0 ? "" : $"{t.Drafts.Count} draft(s)");
        for (var i = 0; i < t.Evaluations.Count; i++)
        {
            var e = t.Evaluations[i];
            Sub($"├ round {i + 1}", e.Pass
                ? $"[green]✓ {Ui.E(e.Kind)} passed[/]"
                : $"[red]✗ {Ui.E(e.Kind)}:[/] [grey]{Ui.E(Short(e.Issues.FirstOrDefault() ?? "", 34))}[/]");
        }
        if (!evalDone && (act.StartsWith("drafting", StringComparison.OrdinalIgnoreCase) || act.StartsWith("evaluating", StringComparison.OrdinalIgnoreCase)))
            Sub($"├ round {t.Drafts.Count + (act.StartsWith("drafting", StringComparison.OrdinalIgnoreCase) ? 1 : 0)}", $"[yellow]{spin} {Ui.E(act)}[/]");
        Link();

        // ⑧ Human checkpoint
        var approval = t.Approval;
        var human = approval switch
        {
            { Decision: "approved" } => S.Done,
            { Decision: "rejected" } => S.Failed,
            _ when act.StartsWith("awaiting", StringComparison.OrdinalIgnoreCase) || act.StartsWith("paused", StringComparison.OrdinalIgnoreCase) => S.Waiting,
            _ => offRoute ? S.Skipped : S.Pending,
        };
        Stage(human, "⑧ Human", approval switch
        {
            { Decision: "approved" } a => $"[green]approved[/] [grey]by {Ui.E(a.Approver)}[/] {Ui.Money(a.Actions.Sum(x => x.Action.AmountCents))}",
            { Decision: "rejected" } a => $"[red]rejected[/] [grey]by {Ui.E(a.Approver)}[/]",
            _ when act.StartsWith("paused", StringComparison.OrdinalIgnoreCase) => "[yellow]paused · --resume to continue[/]",
            _ when human == S.Waiting => $"[yellow]waiting for a person[/] {(t.Proposal is { } p ? Ui.Money(p.Actions.Sum(a => a.AmountCents)) : "")}",
            _ => "",
        });
        Link();

        // ④ Tool-use loop
        var execDone = t.Reached("executed");
        var stopped = t.ExecutionLog.Any(l => l.StartsWith("stopped:"));
        Stage(stopped ? S.Failed : Is(execDone, "executing"), "④ Tool loop",
            t.ExecutionLog.Count == 0 ? "" : $"{t.ExecutionLog.Count(l => l.Contains(") -> "))} tool calls");
        foreach (var line in t.ExecutionLog.TakeLast(8))
        {
            if (line.StartsWith("finished:")) { Sub("└ done", "[green]model finished[/]"); continue; }
            if (line.StartsWith("stopped:")) { Sub("└ stopped", $"[red]{Ui.E(Short(line[8..].Trim(), 30))}[/]"); continue; }
            var name = line.Split('(')[0];
            var result = line.Contains(" -> ") ? line[(line.IndexOf(" -> ", StringComparison.Ordinal) + 4)..] : "";
            var verdict = result.Split(':')[0];
            var color = verdict switch { "OK" => "green", "DUPLICATE" => "blue", "TIMEOUT" or "DENIED" or "ERROR" => "red", _ => "grey" };
            var label = verdict switch
            {
                "OK" => "✓", "DUPLICATE" => "↺ duplicate", "TIMEOUT" => "⚠ TIMEOUT", "DENIED" => "✗ denied", "ERROR" => "✗ error",
                _ when result.StartsWith("completed") => "✓ completed", _ when result.StartsWith("not_found") => "? not found",
                _ => "✓",
            };
            if (result.StartsWith("completed")) color = "green";
            var order = System.Text.RegularExpressions.Regex.Match(line.Split(" -> ")[0], @"orderId=#?(\d+)") is { Success: true } m ? $" #{m.Groups[1].Value}" : "";
            var step = name switch
            {
                "get_order" => "read", "issue_refund" => "refund", "cancel_backorder" => "cancel",
                "get_payment_status" => "status?", "send_customer_message" => "send reply", _ => name,
            };
            Sub($"├ {Short(step + order, 15)}", $"[{color}]{Ui.E(label)}[/]");
        }
        if (act.StartsWith("executing", StringComparison.OrdinalIgnoreCase) && !execDone)
            Sub("├ ...", $"[yellow]{spin} model choosing next step[/]");
        Link();

        // Verify
        Stage(t.Verified switch { true => S.Done, false => S.Failed, _ => Is(false, "verifying") }, "✓ Verify",
            t.Verified switch
            {
                true => "[green]ledger + outbox match the approval[/]",
                false => "[red]ledger does not match the approval[/]",
                _ => "",
            });

        var status = act switch
        {
            "" => t.Status,
            _ => act,
        };
        grid.AddRow(new Text(""), new Text(""));
        grid.AddRow(new Markup("[grey]now[/]"), new Markup(act is "done" ? "[green]done[/]" : $"[yellow]{Ui.E(Short(status, 40))}[/]"));
        grid.AddRow(new Markup("[grey]⑨ task record[/]"),
            new Markup($"[grey]{Ui.E(Path.GetFileName(path ?? ""))} · saved {savedUtc.ToLocalTime():HH:mm:ss}[/]"));

        return Frame(grid, t.Id);
    }

    private static Panel Frame(IRenderable body, string header) =>
        new Panel(body).Header($"[bold] Flow · {Ui.E(header)} [/]").BorderColor(Color.Yellow).Expand();

    private static string Short(string s, int max) => s.Length <= max ? s : s[..(max - 1)] + "…";

    /// <summary>--viz: open the watcher in a Windows Terminal pane to the right of this one.</summary>
    public static void OpenPane()
    {
        var exe = Environment.ProcessPath ?? "";
        var args = new List<string> { "-w", "0", "split-pane", "-V", "--size", "0.4", "-d", Directory.GetCurrentDirectory() };
        if (Path.GetFileNameWithoutExtension(exe).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
            args.AddRange(["dotnet", typeof(FlowView).Assembly.Location, "--watch"]);
        else
            args.AddRange([exe, "--watch"]);

        try
        {
            var psi = new ProcessStartInfo("wt") { UseShellExecute = false };
            foreach (var a in args) psi.ArgumentList.Add(a);
            Process.Start(psi);
            Ui.Note("--viz: opened the flow view in a pane on the right (it stays open for the next runs)");
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            Ui.Warn($"--viz needs Windows Terminal ({ex.Message}). In another terminal run: dotnet run --no-build -- --watch");
        }
    }
}
