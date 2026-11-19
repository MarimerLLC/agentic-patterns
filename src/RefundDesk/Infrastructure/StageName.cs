using System.Collections.Immutable;

namespace RefundDesk.Infrastructure;

/// <summary>
/// Names passed to <see cref="Stage.Enter"/>. A name is "group" or "group:detail"; the meter
/// totals cost per group, and the scripted replay and telemetry key off the full name.
/// </summary>
public static class StageName
{
    public const string Router = "router";
    public const string Orchestrator = "orchestrator";
    public const string OrchestratorPlan = Orchestrator + ":plan";
    public const string OrchestratorFollowUp = Orchestrator + ":followup";
    public const string OrchestratorSynthesize = Orchestrator + ":synthesize";
    public const string Worker = "worker";
    public const string Evaluator = "evaluator";
    public const string Executor = "executor";
    public const string Unscoped = "unscoped";

    private const string WorkerPrefix = Worker + ":";

    /// <summary>The stage name of the research worker reading <paramref name="source"/>.</summary>
    public static string WorkerFor(string source) => WorkerPrefix + source;

    /// <summary>The knowledge source of a worker stage, or null for any other stage.</summary>
    public static string? WorkerSource(string stage) => stage.StartsWith(WorkerPrefix) ? stage[WorkerPrefix.Length..] : null;

    /// <summary>The group part of a stage name ("orchestrator:plan" → "orchestrator").</summary>
    public static string Group(string stage) => stage.Split(':')[0];

    /// <summary>Report order for the economics table.</summary>
    public static readonly ImmutableArray<string> Groups =[Router, Orchestrator, Worker, Evaluator, Executor];
}
