using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using OpenForecourt.Abstractions.Ports;
using OpenForecourt.SiteController.Config;
using OpenForecourt.SiteController.Orchestration;

namespace OpenForecourt.SiteController.Services;

/// <summary>
/// Periodically pings the host link and updates <see cref="HostAvailability"/>, so the site
/// drops into offline mode when the host disappears and climbs back out when it returns.
/// </summary>
/// <remarks>
/// The down→up edge on <see cref="HostAvailability"/> is what fires
/// <see cref="HostAvailability.CameOnline"/>, which the replay service listens for. The probe
/// interval comes from <see cref="SiteOptions.HostProbeInterval"/> and waits on
/// <see cref="IClock"/>, so it is testable.
/// </remarks>
public sealed class HostProber(
    IHostProbe probe, HostAvailability availability, IClock clock, SiteOptions options, ILogger<HostProber> logger)
    : BackgroundService
{
    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            bool up;
            try
            {
                up = await probe.PingAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }

            if (up)
            {
                availability.MarkAvailable();
            }
            else
            {
                logger.LogDebug("Host probe failed; offline mode.");
                availability.MarkUnavailable();
            }

            try
            {
                await clock.Delay(options.HostProbeInterval, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
        }
    }
}
