namespace OpenForecourt.Abstractions.Ports;

/// <summary>
/// The source of "now" and of delays for all testable logic.
/// </summary>
/// <remarks>
/// Every timeout in the system is measured against this clock, never against the real
/// wall clock inside business logic (CLAUDE.md section 6). The production
/// <c>SystemClock</c> delegates to the OS; a <c>FakeClock</c> lets tests advance time
/// deterministically so timeout behaviour is verifiable without real waiting.
/// </remarks>
public interface IClock
{
    /// <summary>The current instant.</summary>
    DateTimeOffset UtcNow { get; }

    /// <summary>
    /// Completes after the given delay as measured by this clock. Under a fake clock this
    /// completes only when the test advances time past the delay.
    /// </summary>
    /// <param name="delay">How long to wait.</param>
    /// <param name="cancellationToken">Cancels the wait.</param>
    Task Delay(TimeSpan delay, CancellationToken cancellationToken);
}
