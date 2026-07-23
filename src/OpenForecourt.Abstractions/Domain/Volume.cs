namespace OpenForecourt.Abstractions.Domain;

/// <summary>
/// A fuel volume held as an integer number of millilitres.
/// </summary>
/// <remarks>
/// Like <see cref="Money"/>, volume is an exact integer quantity, never floating point.
/// A dispenser totalizer counts discrete pulses; millilitres are the natural integer
/// unit and avoid rounding drift when a dispense is summed pulse by pulse.
/// </remarks>
/// <param name="MilliLitres">The volume in millilitres. Non-negative in normal use.</param>
public readonly record struct Volume(long MilliLitres)
{
    /// <summary>Zero volume.</summary>
    public static Volume Zero => new(0);
}
