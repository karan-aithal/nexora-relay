using System.Globalization;
using Serilog.Core;
using Serilog.Events;
using Serilog.Formatting;
using Serilog.Formatting.Display;

namespace OpenForecourt.SiteController.Logging;

/// <summary>
/// A Serilog sink that renders each event, masks any PAN-shaped sequence in the rendered
/// text (<see cref="PanMasker"/>), and writes the result to an inner text writer.
/// </summary>
/// <remarks>
/// Masking lives here — at the sink, after rendering — precisely so it cannot be bypassed by
/// a careless <c>logger.LogInformation(pan)</c> upstream (CLAUDE.md section 7.3). Whatever a
/// caller logs, the bytes that reach the sink's writer are masked. Writes are serialised
/// because a <see cref="System.IO.TextWriter"/> is not guaranteed thread-safe.
/// </remarks>
public sealed class PanMaskingSink(TextWriter writer, ITextFormatter? formatter = null) : ILogEventSink
{
    private readonly ITextFormatter _formatter = formatter ??
        new MessageTemplateTextFormatter(
            "[{Timestamp:HH:mm:ss} {Level:u3}] {Message:lj}{NewLine}{Exception}", CultureInfo.InvariantCulture);
    private readonly Lock _gate = new();

    /// <inheritdoc />
    public void Emit(LogEvent logEvent)
    {
        ArgumentNullException.ThrowIfNull(logEvent);
        using var buffer = new StringWriter(CultureInfo.InvariantCulture);
        _formatter.Format(logEvent, buffer);

        lock (_gate)
        {
            writer.Write(PanMasker.Mask(buffer.ToString()));
            writer.Flush();
        }
    }
}
