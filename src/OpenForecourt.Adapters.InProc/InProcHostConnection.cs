using System.Collections.Concurrent;
using OpenForecourt.Abstractions.Domain;
using OpenForecourt.Abstractions.Ports;

namespace OpenForecourt.Adapters.InProc;

/// <summary>
/// An in-process <see cref="IHostConnection"/>: the CI stand-in for the TCP link to the
/// acquiring host, with a switch to simulate the host going away (CLAUDE.md section 3).
/// </summary>
/// <remarks>
/// <para>
/// When <see cref="Available"/> is false, <see cref="SendAsync"/> returns
/// <see cref="AuthorisationOutcome.NoResponse"/> — exactly what a real timeout yields — so
/// offline-mode and reversal logic can be driven deterministically. The decision for an
/// available host comes from <see cref="Decide"/>, defaulting to approve.
/// </para>
/// <para>
/// Every delivered request is recorded, and the host dedupes on transaction id, so a
/// replayed request authorises at most once — the property the exactly-once replay test
/// asserts against.
/// </para>
/// </remarks>
public sealed class InProcHostConnection : IHostConnection, IHostProbe
{
    /// <inheritdoc />
    public Task<bool> PingAsync(CancellationToken cancellationToken) => Task.FromResult(Available);

    private readonly ConcurrentDictionary<Guid, HostResponse> _seen = new();
    private readonly List<FinancialRequest> _requests = [];
    private readonly Lock _gate = new();

    /// <summary>Whether the host is reachable. Flip to false to simulate a host outage.</summary>
    public bool Available { get; set; } = true;

    /// <summary>The decision for an available host. Defaults to approve everything.</summary>
    public Func<FinancialRequest, AuthorisationOutcome> Decide { get; set; } =
        static _ => AuthorisationOutcome.Approved;

    /// <summary>Every request the host actually received (i.e. while available).</summary>
    public IReadOnlyList<FinancialRequest> Requests
    {
        get { lock (_gate) { return _requests.ToArray(); } }
    }

    /// <inheritdoc />
    public Task<HostResponse> SendAsync(FinancialRequest request, CancellationToken cancellationToken)
    {
        if (!Available)
        {
            return Task.FromResult(new HostResponse(AuthorisationOutcome.NoResponse, null));
        }

        // Idempotent at the host: a repeated transaction id yields the first decision again.
        var response = _seen.GetOrAdd(request.TransactionId, _ =>
        {
            lock (_gate)
            {
                _requests.Add(request);
            }

            var outcome = Decide(request);
            string? authCode = outcome == AuthorisationOutcome.Approved ? "A12345" : null;
            return new HostResponse(outcome, authCode);
        });

        return Task.FromResult(response);
    }
}
