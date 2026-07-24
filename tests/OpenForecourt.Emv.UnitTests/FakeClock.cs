using OpenForecourt.Abstractions.Ports;

namespace OpenForecourt.Emv.UnitTests;

/// <summary>A clock frozen at a fixed instant, so date-dependent EMV checks are deterministic.</summary>
internal sealed class FakeClock(DateTimeOffset now) : IClock
{
    public DateTimeOffset UtcNow { get; } = now;

    public Task Delay(TimeSpan delay, CancellationToken cancellationToken) => Task.CompletedTask;
}
