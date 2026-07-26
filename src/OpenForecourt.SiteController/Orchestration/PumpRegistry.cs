using System.Collections.Concurrent;
using OpenForecourt.Abstractions.Domain;
using OpenForecourt.SiteController.Config;

namespace OpenForecourt.SiteController.Orchestration;

/// <summary>A point-in-time view of one pump, as broadcast to dashboards.</summary>
/// <param name="PumpId">The pump number.</param>
/// <param name="State">Its current lifecycle state.</param>
/// <param name="TransactionId">The transaction in progress, if any.</param>
/// <param name="AuthorisedMinor">The authorised amount in minor units, if any.</param>
/// <param name="DispensedMillilitres">How much has been dispensed so far.</param>
/// <param name="Currency">Currency of <paramref name="AuthorisedMinor"/>.</param>
public readonly record struct PumpSnapshot(
    int PumpId, PumpState State, Guid? TransactionId, long AuthorisedMinor, int DispensedMillilitres, string Currency)
{
    /// <summary>Value of the fuel delivered so far, in minor units, metered by the firmware.</summary>
    public long DispensedMinor { get; init; }

    /// <summary>The selected grade code, e.g. <c>U95</c>.</summary>
    public string? GradeCode { get; init; }

    /// <summary>Unit price the dispenser is charging, in minor units per litre.</summary>
    public int UnitPricePerLitreMinor { get; init; }

    /// <summary>Card brand read from the presented card, for the tile.</summary>
    public string? CardBrand { get; init; }

    /// <summary>When the current session started, for the elapsed-time readout.</summary>
    public DateTimeOffset? StartedAt { get; init; }

    /// <summary>The OFP-1 link state to the pump firmware: <c>Connected</c>, <c>Connecting</c>, …</summary>
    public string Link { get; init; } = "Unknown";

    /// <summary>The dispenser FSM state the firmware last reported.</summary>
    public string DispenserState { get; init; } = "Idle";

    /// <summary>True while the nozzle is out of its holster.</summary>
    public bool NozzleUp { get; init; }
}

/// <summary>
/// The live state of every pump, held in memory and updated as transactions progress. It is
/// the source the SignalR hub broadcasts from and resynchronises reconnecting clients against.
/// </summary>
/// <remarks>
/// This is deliberately just state plus a change event — no SignalR, no web types — so it is
/// trivially constructible in tests and the broadcasting concern stays outside it. Dispense
/// progress here is a presentation animation only; the money lifecycle lives in the journal
/// (see <see cref="TransactionService"/>).
/// </remarks>
public sealed class PumpRegistry
{
    private readonly ConcurrentDictionary<int, PumpSnapshot> _pumps = new();
    private readonly string _currency;

    /// <summary>Creates the registry with <see cref="SiteOptions.PumpCount"/> idle pumps.</summary>
    public PumpRegistry(SiteOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _currency = options.Currency;
        for (int id = 1; id <= options.PumpCount; id++)
        {
            _pumps[id] = new PumpSnapshot(id, PumpState.Idle, null, 0, 0, _currency);
        }
    }

    /// <summary>Raised whenever a pump's snapshot changes.</summary>
    public event EventHandler<PumpSnapshot>? PumpChanged;

    /// <summary>The current snapshot of every pump, ordered by id.</summary>
    public IReadOnlyList<PumpSnapshot> Snapshot() => _pumps.Values.OrderBy(p => p.PumpId).ToArray();

    /// <summary>True when the pump id is within range.</summary>
    public bool Exists(int pumpId) => _pumps.ContainsKey(pumpId);

    /// <summary>Returns one pump's snapshot, or null if the id is unknown.</summary>
    public PumpSnapshot? Get(int pumpId) => _pumps.TryGetValue(pumpId, out var p) ? p : null;

    /// <summary>Sets a pump's state and (optionally) the transaction and authorised amount, then broadcasts.</summary>
    public void SetState(int pumpId, PumpState state, Guid? transactionId = null, Money? authorised = null)
    {
        var updated = _pumps.AddOrUpdate(
            pumpId,
            _ => new PumpSnapshot(pumpId, state, transactionId, authorised?.Minor ?? 0, 0, _currency),
            (_, existing) => existing with
            {
                State = state,
                TransactionId = transactionId ?? existing.TransactionId,
                AuthorisedMinor = authorised?.Minor ?? existing.AuthorisedMinor,
            });
        PumpChanged?.Invoke(this, updated);
    }

    /// <summary>Updates dispense progress for the pump and broadcasts.</summary>
    public void SetDispensed(int pumpId, int millilitres)
    {
        if (_pumps.TryGetValue(pumpId, out var existing))
        {
            var updated = existing with { DispensedMillilitres = millilitres };
            _pumps[pumpId] = updated;
            PumpChanged?.Invoke(this, updated);
        }
    }

    /// <summary>
    /// Applies an arbitrary change to a pump's snapshot and broadcasts it. One mutator rather than
    /// a setter per field: the fleet updates several fields at once from a single pump event, and a
    /// per-field setter would broadcast a torn snapshot for each one.
    /// </summary>
    public void Update(int pumpId, Func<PumpSnapshot, PumpSnapshot> change)
    {
        ArgumentNullException.ThrowIfNull(change);
        if (!_pumps.TryGetValue(pumpId, out var existing))
        {
            return;
        }

        var updated = change(existing);
        _pumps[pumpId] = updated;
        PumpChanged?.Invoke(this, updated);
    }

    /// <summary>Returns the pump to idle, clearing its transaction, and broadcasts.</summary>
    /// <remarks>Grade, unit price and link state survive: they describe the dispenser, not the sale.</remarks>
    public void Clear(int pumpId)
    {
        var idle = new PumpSnapshot(pumpId, PumpState.Idle, null, 0, 0, _currency);
        var updated = _pumps.TryGetValue(pumpId, out var existing)
            ? idle with
            {
                GradeCode = existing.GradeCode,
                UnitPricePerLitreMinor = existing.UnitPricePerLitreMinor,
                Link = existing.Link,
                DispenserState = existing.DispenserState,
            }
            : idle;
        _pumps[pumpId] = updated;
        PumpChanged?.Invoke(this, updated);
    }
}
