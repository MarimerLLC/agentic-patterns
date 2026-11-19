using System.Globalization;
using Spectre.Console;

namespace RefundDesk.Infrastructure;

/// <summary>Console presentation only. Nothing in here makes decisions.</summary>
public static class Ui
{
    private static readonly CultureInfo Us = CultureInfo.GetCultureInfo("en-US");

    public static string Money(int cents) => (cents / 100m).ToString("C", Us);

    public static string E(string? text) => Markup.Escape(text ?? "");

    public static void StageHeader(string pattern, string title, string why)
    {
        AnsiConsole.WriteLine();
        AnsiConsole.Write(new Rule($"[bold yellow]{pattern}[/] [bold]{E(title)}[/]").LeftJustified().RuleStyle("grey"));
        AnsiConsole.MarkupLine($"[grey]{E(why)}[/]");
    }

    public static void Resumed(string what) =>
        AnsiConsole.MarkupLine($"[blue]↻ resumed from task record:[/] [grey]{E(what)}[/]");

    public static void Note(string text) => AnsiConsole.MarkupLine($"[grey]  {E(text)}[/]");
    public static void Ok(string text) => AnsiConsole.MarkupLine($"[green]  ✓[/] {E(text)}");
    public static void Warn(string text) => AnsiConsole.MarkupLine($"[yellow]  ![/] {E(text)}");
    public static void Fail(string text) => AnsiConsole.MarkupLine($"[red]  ✗[/] {E(text)}");

    public static Task<T> Spin<T>(string label, Func<Task<T>> work) =>
        AnsiConsole.Status().Spinner(Spinner.Known.Dots).SpinnerStyle(Style.Parse("yellow"))
            .StartAsync(label, _ => work());

    public static void Economics(Meter meter, bool scripted, TimeSpan wall)
    {
        AnsiConsole.WriteLine();
        AnsiConsole.Write(new Rule("[bold]Economics for this task[/]").LeftJustified().RuleStyle("grey"));

        var table = new Table().Border(TableBorder.Rounded).BorderColor(Color.Grey)
            .AddColumn("Stage").AddColumn(new TableColumn("Model calls").RightAligned())
            .AddColumn(new TableColumn(scripted ? "Tokens in (est.)" : "Tokens in").RightAligned())
            .AddColumn(new TableColumn(scripted ? "Tokens out (est.)" : "Tokens out").RightAligned())
            .AddColumn(new TableColumn("Tool calls").RightAligned())
            .AddColumn(new TableColumn("Model time").RightAligned());

        string[] order = ["router", "orchestrator", "worker", "evaluator", "executor"];
        foreach (var (stage, s) in meter.Stages.OrderBy(kv => Array.IndexOf(order, kv.Key) is var i && i < 0 ? 99 : i))
            table.AddRow(E(stage), $"{s.ModelCalls}", $"{s.InputTokens:N0}", $"{s.OutputTokens:N0}", $"{s.ToolCalls}", $"{s.ElapsedMs / 1000.0:F1}s");

        var all = meter.Stages.Values;
        table.AddRow("[bold]total[/]", $"[bold]{all.Sum(s => s.ModelCalls)}[/]", $"[bold]{all.Sum(s => s.InputTokens):N0}[/]",
            $"[bold]{all.Sum(s => s.OutputTokens):N0}[/]", $"[bold]{all.Sum(s => s.ToolCalls)}[/]", $"[bold]{wall.TotalSeconds:F1}s wall[/]");
        AnsiConsole.Write(table);

        AnsiConsole.MarkupLine(
            $"[grey]Evaluator rounds:[/] {meter.EvaluatorRounds}   [grey]Loop iterations:[/] {meter.LoopIterations}   " +
            $"[grey]Effect retries:[/] {meter.EffectRetries}   [grey]Human interventions:[/] {meter.HumanInterventions}");
    }
}
