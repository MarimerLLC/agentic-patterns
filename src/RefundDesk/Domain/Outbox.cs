using System.Text.Json;
using RefundDesk.Infrastructure;

namespace RefundDesk.Domain;

public record OutboxEntry(string IdempotencyKey, string MessageId, string CustomerId, string Text, DateTimeOffset At);

/// <summary>
/// Stand-in for an email/SMS provider. Sending a message is a side effect too, so it gets the
/// same treatment as money: a durable record keyed by an idempotency key. After a crash and
/// --resume, the executor's second send returns the original message instead of mailing the
/// customer again.
/// </summary>
public sealed class Outbox
{
    private readonly string _path;
    private readonly List<OutboxEntry> _sent;
    private readonly object _gate = new();

    public Outbox(string dataDir)
    {
        _path = Path.Combine(dataDir, "messages-outbox.json");
        _sent = File.Exists(_path)
            ? JsonSerializer.Deserialize<List<OutboxEntry>>(File.ReadAllText(_path), Json.Options) ?? []
            : [];
    }

    public IReadOnlyList<OutboxEntry> Sent { get { lock (_gate) return _sent.ToList(); } }

    public (OutboxEntry Entry, bool Duplicate) Send(string key, string customerId, string text)
    {
        lock (_gate)
        {
            var existing = _sent.FirstOrDefault(e => e.IdempotencyKey == key);
            if (existing is not null)
                return (existing, true);

            var entry = new OutboxEntry(key, $"M-{Random.Shared.Next(10000, 99999)}", customerId, text, DateTimeOffset.Now);
            _sent.Add(entry);
            File.WriteAllText(_path, JsonSerializer.Serialize(_sent, Json.Options));
            return (entry, false);
        }
    }
}
