using System.Text.Json;
using RefundDesk.Infrastructure;

namespace RefundDesk.Domain;

public record LedgerEntry(string IdempotencyKey, string RefundId, string OrderId, int AmountCents, string Kind, DateTimeOffset At);

/// <summary>
/// Stand-in for an external payment system. It is the system of record for money,
/// so its ledger is persisted to disk and survives restarts. Every effect takes an
/// idempotency key: replaying a key returns the original result instead of paying twice.
/// </summary>
public sealed class PaymentGateway
{
    private readonly string _path;
    private readonly List<LedgerEntry> _ledger;
    private readonly HashSet<string> _timedOutOnce = [];
    private readonly object _gate = new();

    /// <summary>When true, the first effect of the run is applied and then times out.</summary>
    public bool Chaos { get; set; }

    public PaymentGateway(string dataDir)
    {
        _path = Path.Combine(dataDir, "payments-ledger.json");
        _ledger = File.Exists(_path)
            ? JsonSerializer.Deserialize<List<LedgerEntry>>(File.ReadAllText(_path), Json.Options) ?? []
            : [];
    }

    public IReadOnlyList<LedgerEntry> Ledger { get { lock (_gate) return _ledger.ToList(); } }

    public (LedgerEntry Entry, bool Duplicate) Refund(string key, string orderId, int amountCents, string kind)
    {
        lock (_gate)
        {
            var existing = _ledger.FirstOrDefault(e => e.IdempotencyKey == key);
            if (existing is not null)
                return (existing, true);

            var entry = new LedgerEntry(key, $"R-{Random.Shared.Next(10000, 99999)}", orderId, amountCents, kind, DateTimeOffset.Now);
            _ledger.Add(entry);
            Save();

            // Simulate the nastiest failure: the effect happened, but the caller never hears back.
            if (Chaos && _timedOutOnce.Count == 0 && _timedOutOnce.Add(key))
                throw new TimeoutException("Payment gateway did not respond within 30s.");

            return (entry, false);
        }
    }

    public LedgerEntry? Find(string key)
    {
        lock (_gate) return _ledger.FirstOrDefault(e => e.IdempotencyKey == key);
    }

    private void Save() => File.WriteAllText(_path, JsonSerializer.Serialize(_ledger, Json.Options));
}

/// <summary>Order system of record (in memory; reseeded per run, updated from the ledger).</summary>
public sealed class OrderBook
{
    private readonly Dictionary<string, Order> _orders;

    public OrderBook(PaymentGateway payments)
    {
        _orders = SampleData.Orders().ToDictionary(o => o.Id);
        foreach (var e in payments.Ledger)
            MarkRefunded(e.OrderId, e.Kind);
    }

    public Order? Find(string id) => _orders.GetValueOrDefault(id.TrimStart('#'));

    public void MarkRefunded(string orderId, string kind)
    {
        if (_orders.TryGetValue(orderId, out var o))
            _orders[orderId] = o with { Status = kind == ActionKind.CancelBackorder ? OrderStatus.Cancelled : OrderStatus.Refunded };
    }
}
