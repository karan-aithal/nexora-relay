namespace OpenForecourt.Abstractions.Domain;

/// <summary>
/// A monetary amount held as an integer number of minor units (e.g. cents, pence)
/// together with an ISO 4217 currency code.
/// </summary>
/// <remarks>
/// Money is <b>never</b> represented as <see cref="decimal"/> or <see cref="double"/>
/// in this system (CLAUDE.md section 6): binary/decimal floating point cannot represent
/// most currency values exactly, and payment reconciliation demands exact arithmetic.
/// All amounts are integer minor units; conversion to a display string with a decimal
/// point is a presentation concern, not a storage one.
/// </remarks>
/// <param name="Minor">
/// The amount in minor units. May be negative (e.g. a refund or reversal).
/// </param>
/// <param name="CurrencyCode">The ISO 4217 alphabetic currency code, e.g. "GBP", "EUR".</param>
public readonly record struct Money(long Minor, string CurrencyCode)
{
    /// <summary>Zero in the given currency.</summary>
    public static Money Zero(string currencyCode) => new(0, currencyCode);
}
