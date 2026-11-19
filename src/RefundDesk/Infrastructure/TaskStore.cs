using System.Text.Json;
using RefundDesk.Domain;

namespace RefundDesk.Infrastructure;

/// <summary>
/// ⑨ Durable task record. The model's context window is NOT the memory of this task:
/// this file is. Every stage writes its typed artifact here before the next one starts,
/// so the run can stop (crash, Ctrl+C, waiting on a human) and resume where it left off.
/// </summary>
public sealed class TaskRecord
{
    public required string Id { get; init; }
    public required CustomerRequest Request { get; init; }
    /// <summary>The last <see cref="TaskPhase"/> completed.</summary>
    public string Status { get; set; } = TaskPhase.New;
    public DateTimeOffset Created { get; init; } = DateTimeOffset.Now;
    public DateTimeOffset Updated { get; set; } = DateTimeOffset.Now;

    public RouteDecision? Route { get; set; }
    public string? RoutedBy { get; set; }
    public HandoffPackage? Handoff { get; set; }
    public List<Order> Orders { get; set; } = [];
    public ResearchPlan? Plan { get; set; }
    public List<Finding> Findings { get; set; } = [];
    public List<Proposal> Drafts { get; set; } = [];
    public List<Evaluation> Evaluations { get; set; } = [];
    public Approval? Approval { get; set; }
    public List<string> ExecutionLog { get; set; } = [];
    public string? Outcome { get; set; }
    /// <summary>What the run is doing right now (read by the --watch flow view).</summary>
    public string? Activity { get; set; }
    /// <summary>Result of the ledger/outbox read-back; null until verified.</summary>
    public bool? Verified { get; set; }
    /// <summary>OpenTelemetry root span of each run of this task, so a resumed run's trace links to the earlier ones.</summary>
    public List<string> Traces { get; set; } = [];

    public Proposal? Proposal => Drafts.LastOrDefault();

    /// <summary>True once the task has passed the given <see cref="TaskPhase"/>.</summary>
    public bool Reached(string phase) =>
        TaskPhase.Order.IndexOf(Status) >= TaskPhase.Order.IndexOf(phase) && TaskPhase.Order.IndexOf(phase) >= 0;
}

public sealed class TaskStore(string dataDir)
{
    private readonly string _dir = Directory.CreateDirectory(Path.Combine(dataDir, "tasks")).FullName;

    public TaskRecord Create(CustomerRequest request)
    {
        var id = $"T-{DateTime.Now:MMdd}-{Random.Shared.Next(0x1000, 0xFFFF):x4}";
        var record = new TaskRecord { Id = id, Request = request };
        Save(record);
        return record;
    }

    public void Save(TaskRecord record)
    {
        record.Updated = DateTimeOffset.Now;
        var path = PathFor(record.Id);
        var temp = path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(record, Json.Options));
        File.Move(temp, path, overwrite: true);   // atomic replace: never a half-written record
    }

    public TaskRecord? Load(string? id)
    {
        var path = id is null
            ? new DirectoryInfo(_dir).GetFiles("T-*.json").OrderByDescending(f => f.LastWriteTimeUtc).FirstOrDefault()?.FullName
            : PathFor(id);
        return path is not null && File.Exists(path)
            ? JsonSerializer.Deserialize<TaskRecord>(File.ReadAllText(path), Json.Options)
            : null;
    }

    public string PathFor(string id) => Path.Combine(_dir, $"{id}.json");
}
