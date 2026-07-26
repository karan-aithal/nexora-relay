using System.Collections.Concurrent;

namespace OpenForecourt.SiteController.Faults;

/// <summary>The fault console's view of what is currently armed.</summary>
/// <param name="HostLinkDown">The acquirer link is severed.</param>
/// <param name="HostLatencyMs">Artificial latency added to every host exchange.</param>
/// <param name="ForcedResponsePan">Test PAN steering the host simulator to a chosen response code.</param>
/// <param name="Suspended">Authorisations are held between the journal write and the host request.</param>
/// <param name="CrcArmedPumps">Pumps whose next outbound OFP-1 frame will be corrupted.</param>
/// <param name="PoweredOffPumps">Pumps whose firmware process has been killed.</param>
public sealed record FaultState(
    bool HostLinkDown,
    int HostLatencyMs,
    string? ForcedResponsePan,
    bool Suspended,
    IReadOnlyList<int> CrcArmedPumps,
    IReadOnlyList<int> PoweredOffPumps);

/// <summary>
/// The armed-fault register. Every field here is read by a real component on its real path —
/// nothing in this class simulates an <i>effect</i>, it only records an operator's intent that
/// the host link, the pump wire or the firmware process then honours (CLAUDE.md Phase 6:
/// "each control triggers a real fault in the running system, not a UI mock").
/// </summary>
public sealed class FaultInjector
{
    private readonly ConcurrentDictionary<int, byte> _crcArmed = new();
    private readonly ConcurrentDictionary<int, byte> _poweredOff = new();
    private readonly Lock _gate = new();
    private TaskCompletionSource? _suspension;

    /// <summary>Raised whenever the armed set changes, so the dashboard can mirror it live.</summary>
    public event EventHandler<FaultState>? Changed;

    /// <summary>True while the acquirer link is severed.</summary>
    public bool HostLinkDown { get; private set; }

    /// <summary>Artificial latency applied to each host exchange, in milliseconds.</summary>
    public int HostLatencyMs { get; private set; }

    /// <summary>
    /// The PAN placed on the next host requests. The host simulator's rules map specific test PANs
    /// to specific response codes, so forcing a decline means sending the PAN the acquirer declines —
    /// the decline is genuinely produced by the host, not fabricated locally.
    /// </summary>
    public string? ForcedResponsePan { get; private set; }

    /// <summary>Severs or restores the acquirer link.</summary>
    public void SetHostLinkDown(bool down)
    {
        HostLinkDown = down;
        Publish();
    }

    /// <summary>Sets the artificial host latency in milliseconds (0 clears it).</summary>
    public void SetHostLatency(int milliseconds)
    {
        HostLatencyMs = Math.Max(0, milliseconds);
        Publish();
    }

    /// <summary>Steers the host simulator to a response code by choosing the PAN sent (null clears).</summary>
    public void SetForcedResponsePan(string? pan)
    {
        ForcedResponsePan = string.IsNullOrWhiteSpace(pan) ? null : pan;
        Publish();
    }

    /// <summary>Arms a single-shot CRC corruption on the pump's next outbound frame.</summary>
    public void ArmCrcCorruption(int pumpId)
    {
        _crcArmed[pumpId] = 0;
        Publish();
    }

    /// <summary>Takes the armed CRC corruption for a pump, if any. Single-shot.</summary>
    public bool ConsumeCrcCorruption(int pumpId)
    {
        bool armed = _crcArmed.TryRemove(pumpId, out _);
        if (armed)
        {
            Publish();
        }

        return armed;
    }

    /// <summary>Records that a pump's firmware process is down (or back up).</summary>
    public void SetPoweredOff(int pumpId, bool off)
    {
        if (off)
        {
            _poweredOff[pumpId] = 0;
        }
        else
        {
            _poweredOff.TryRemove(pumpId, out _);
        }

        Publish();
    }

    /// <summary>
    /// Suspends the site controller mid-authorisation: the journal has the intent and the
    /// request-sent record, and the host request is held. This is the exact window
    /// <see cref="Services.RecoveryService"/> has to reconcile if the process dies here.
    /// </summary>
    public void Suspend()
    {
        lock (_gate)
        {
            _suspension ??= new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        Publish();
    }

    /// <summary>Releases every authorisation held by <see cref="Suspend"/>.</summary>
    public void Resume()
    {
        TaskCompletionSource? held;
        lock (_gate)
        {
            held = _suspension;
            _suspension = null;
        }

        held?.TrySetResult();
        Publish();
    }

    /// <summary>Awaits release if the site controller is suspended; returns immediately otherwise.</summary>
    public Task WaitIfSuspendedAsync(CancellationToken cancellationToken)
    {
        Task? wait;
        lock (_gate)
        {
            wait = _suspension?.Task;
        }

        return wait is null ? Task.CompletedTask : wait.WaitAsync(cancellationToken);
    }

    /// <summary>The current armed set.</summary>
    public FaultState Snapshot()
    {
        lock (_gate)
        {
            return new FaultState(
                HostLinkDown, HostLatencyMs, ForcedResponsePan, _suspension is not null,
                [.. _crcArmed.Keys.Order()], [.. _poweredOff.Keys.Order()]);
        }
    }

    private void Publish() => Changed?.Invoke(this, Snapshot());
}
