using OpenForecourt.Abstractions.Ports;

namespace OpenForecourt.SiteController.UnitTests;

/// <summary>A clock frozen at a fixed instant; delays complete immediately so tests never wait.</summary>
internal sealed class FakeClock(DateTimeOffset now) : IClock
{
    public DateTimeOffset UtcNow { get; set; } = now;

    public Task Delay(TimeSpan delay, CancellationToken cancellationToken) => Task.CompletedTask;
}
