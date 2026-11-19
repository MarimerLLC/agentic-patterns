namespace RefundDesk.Infrastructure;

/// <summary>
/// What a run is doing right now (<see cref="TaskRecord.Activity"/>). The flow view matches
/// on these as prefixes, so a running stage may add detail after them ("drafting round 2").
/// </summary>
public static class TaskActivity
{
    public const string Routing = "routing";
    public const string Planning = "planning research";
    public const string Researching = "researching";
    public const string Drafting = "drafting";
    public const string Evaluating = "evaluating";
    public const string FollowUp = "follow-up";
    public const string AwaitingApproval = "awaiting approval";
    public const string Paused = "paused for a human";
    public const string Executing = "executing approved actions";
    public const string Verifying = "verifying";
    public const string Done = "done";
}
