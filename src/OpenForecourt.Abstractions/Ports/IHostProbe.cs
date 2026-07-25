namespace OpenForecourt.Abstractions.Ports;

/// <summary>
/// A liveness check for the acquiring host link, separate from authorisation so it never
/// creates a financial message.
/// </summary>
/// <remarks>
/// The real implementation sends an ISO 8583 <c>0800</c> network-management echo; the
/// in-process implementation reports its simulated availability. Used by the health check
/// and by the prober that flips the site into and out of offline mode.
/// </remarks>
public interface IHostProbe
{
    /// <summary>True when the host answered the probe.</summary>
    Task<bool> PingAsync(CancellationToken cancellationToken);
}
