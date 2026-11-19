using System.Collections.Immutable;

namespace RefundDesk.Infrastructure;

/// <summary>
/// Values of <see cref="TaskRecord.Status"/>: the last stage the durable task has completed.
/// (Not called TaskStatus, to avoid a clash with System.Threading.Tasks.TaskStatus.)
/// </summary>
public static class TaskPhase
{
    public const string New = "new";
    public const string Routed = "routed";
    public const string Researched = "researched";
    public const string Evaluated = "evaluated";
    public const string Approved = "approved";
    public const string Executed = "executed";

    /// <summary>Pipeline order, so a record can say whether it has passed a phase.</summary>
    public static readonly ImmutableArray<string> Order =[New, Routed, Researched, Evaluated, Approved, Executed];
}
