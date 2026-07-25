using Microsoft.Extensions.Diagnostics.HealthChecks;
using OpenForecourt.Abstractions.Ports;
using OpenForecourt.SiteController.Orchestration;

namespace OpenForecourt.SiteController.Services;

/// <summary>Healthy when the journal is readable — the crash-safety record must always be reachable.</summary>
public sealed class JournalHealthCheck(ITransactionJournal journal) : IHealthCheck
{
    /// <inheritdoc />
    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        try
        {
            var incomplete = await journal.ReadIncompleteAsync(cancellationToken).ConfigureAwait(false);
            return HealthCheckResult.Healthy($"journal readable, {incomplete.Count} in-flight");
        }
#pragma warning disable CA1031 // a health check reports any failure rather than propagating it
        catch (Exception ex)
#pragma warning restore CA1031
        {
            return HealthCheckResult.Unhealthy("journal unreadable", ex);
        }
    }
}

/// <summary>
/// Reflects the host link. A down host is <b>degraded</b>, not unhealthy: the site keeps
/// working in offline mode, so it should not be pulled out of rotation.
/// </summary>
public sealed class HostLinkHealthCheck(HostAvailability availability, OfflinePolicy offlinePolicy) : IHealthCheck
{
    /// <inheritdoc />
    public Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context, CancellationToken cancellationToken = default) =>
        Task.FromResult(availability.IsAvailable
            ? HealthCheckResult.Healthy("host link up")
            : HealthCheckResult.Degraded($"host link down; offline exposure {offlinePolicy.CurrentExposure}"));
}

/// <summary>Healthy when a dispatch backend is wired; the concrete backend fails fast at startup if unreachable.</summary>
public sealed class DispatchHealthCheck(ITransactionDispatch dispatch) : IHealthCheck
{
    /// <inheritdoc />
    public Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context, CancellationToken cancellationToken = default) =>
        Task.FromResult(HealthCheckResult.Healthy(dispatch.GetType().Name));
}
