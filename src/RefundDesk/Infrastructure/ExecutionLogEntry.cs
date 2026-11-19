namespace RefundDesk.Infrastructure;

/// <summary>
/// Line formats in <see cref="TaskRecord.ExecutionLog"/>: written by the executor, parsed by
/// the flow view. A tool call is "name(args) -> result".
/// </summary>
public static class ExecutionLogEntry
{
    public const string Finished = "finished:";
    public const string Stopped = "stopped:";
    public const string ResultSeparator = " -> ";
}
