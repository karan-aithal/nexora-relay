using System.Globalization;

namespace OpenForecourt.SiteController.Orchestration;

/// <summary>
/// Issues the six-digit System Trace Audit Number (ISO 8583 field 11) that makes each host
/// request uniquely identifiable, wrapping after 999999.
/// </summary>
public sealed class StanSequence
{
    private int _value;

    /// <summary>Returns the next STAN as a zero-padded six-digit string.</summary>
    public string Next()
    {
        int next = Interlocked.Increment(ref _value) % 1_000_000;
        return next.ToString("D6", CultureInfo.InvariantCulture);
    }
}
