using Microsoft.AspNetCore.SignalR;
using OpenForecourt.SiteController.Contracts;

namespace OpenForecourt.SiteController.Realtime;

/// <summary>
/// The dashboard's real-time feed: live pump state, transaction lifecycle events, and a
/// snapshot for (re)synchronisation.
/// </summary>
/// <remarks>
/// On connect, the client is registered with the <see cref="PerConnectionDispatcher"/> (which
/// isolates it behind its own drop-oldest queue) and immediately sent a full snapshot.
/// <see cref="GetSnapshot"/> lets a client that has just reconnected pull authoritative state
/// and reconcile whatever live frames it dropped while away.
/// </remarks>
public sealed class ForecourtHub(PerConnectionDispatcher dispatcher, SnapshotProvider snapshots) : Hub
{
    /// <inheritdoc />
    public override async Task OnConnectedAsync()
    {
        dispatcher.Add(Context.ConnectionId);
        var snapshot = await snapshots.BuildAsync(Context.ConnectionAborted).ConfigureAwait(false);
        await Clients.Caller.SendAsync("Snapshot", snapshot, Context.ConnectionAborted).ConfigureAwait(false);
        await base.OnConnectedAsync().ConfigureAwait(false);
    }

    /// <inheritdoc />
    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        await dispatcher.RemoveAsync(Context.ConnectionId).ConfigureAwait(false);
        await base.OnDisconnectedAsync(exception).ConfigureAwait(false);
    }

    /// <summary>Returns the current site snapshot for client-side resynchronisation after a reconnect.</summary>
    public Task<SiteSnapshot> GetSnapshot() => snapshots.BuildAsync(Context.ConnectionAborted);
}
