using OpenForecourt.Abstractions.Ports;

namespace OpenForecourt.SiteController.Faults;

/// <summary>
/// Wraps the acquirer link so the fault console can sever it, slow it, or freeze an
/// authorisation in flight.
/// </summary>
/// <remarks>
/// <para>
/// A decorator, not a flag inside <see cref="Orchestration.TransactionService"/>. The orchestrator
/// stays free of any notion of injected faults: what it sees is a host that times out, or one that
/// takes eight seconds to answer — indistinguishable from the real thing, which is the point. Every
/// downstream consequence (the prober marking the host down, the fallback to offline authorisation,
/// the reversal advice on no response) is produced by the untouched production path.
/// </para>
/// <para>
/// The suspension gate sits <b>after</b> the journal has recorded <c>HostRequestSent</c> and
/// <b>before</b> the request reaches the wire. That is deliberately the one window where a crash
/// leaves the host's state genuinely unknown, so "suspend the site controller mid-authorisation"
/// parks the system exactly where recovery is hardest.
/// </para>
/// </remarks>
public sealed class FaultingHostConnection(IHostConnection inner, IHostProbe probe, FaultInjector faults, IClock clock)
    : IHostConnection, IHostProbe
{
    /// <inheritdoc />
    public async Task<HostResponse> SendAsync(FinancialRequest request, CancellationToken cancellationToken)
    {
        await faults.WaitIfSuspendedAsync(cancellationToken).ConfigureAwait(false);

        if (faults.HostLatencyMs > 0)
        {
            await clock.Delay(TimeSpan.FromMilliseconds(faults.HostLatencyMs), cancellationToken).ConfigureAwait(false);
        }

        // A severed link is silence, not an exception: the caller must handle the same
        // "host state unknown" case a cut cable produces.
        return faults.HostLinkDown
            ? new HostResponse(AuthorisationOutcome.NoResponse, null)
            : await inner.SendAsync(request, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public Task<bool> PingAsync(CancellationToken cancellationToken) =>
        faults.HostLinkDown ? Task.FromResult(false) : probe.PingAsync(cancellationToken);
}
