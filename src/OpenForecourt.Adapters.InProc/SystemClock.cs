using OpenForecourt.Abstractions.Ports;

namespace OpenForecourt.Adapters.InProc;

/// <summary>
/// The real clock: <see cref="IClock"/> backed by the operating system.
/// </summary>
/// <remarks>
/// Production code takes <see cref="IClock"/>, never <see cref="DateTimeOffset.UtcNow"/>
/// directly, so tests can substitute a fake and drive timeouts deterministically
/// (CLAUDE.md section 6).
/// </remarks>
public sealed class SystemClock : IClock
{
    /// <summary>A shared instance; the type is stateless.</summary>
    public static SystemClock Instance { get; } = new();

    /// <inheritdoc />
    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;

    /// <inheritdoc />
    public Task Delay(TimeSpan delay, CancellationToken cancellationToken) => Task.Delay(delay, cancellationToken);
}
