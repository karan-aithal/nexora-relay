using OpenForecourt.Abstractions.Domain;
using OpenForecourt.SiteController.Orchestration;

namespace OpenForecourt.SiteController.Contracts;

/// <summary>The REST/SignalR view of one transaction. Carries a token reference, never a PAN.</summary>
/// <param name="TransactionId">The transaction id.</param>
/// <param name="PumpId">The pump.</param>
/// <param name="Status">The recovery lifecycle status.</param>
/// <param name="State">The pump-facing state.</param>
/// <param name="AmountMinor">Amount in minor units.</param>
/// <param name="Currency">ISO 4217 currency.</param>
/// <param name="AuthorisationCode">Authorisation code when approved.</param>
/// <param name="Offline">Whether it was authorised offline.</param>
/// <param name="CreatedAt">When it opened.</param>
/// <param name="UpdatedAt">When it last changed.</param>
public sealed record TransactionView(
    Guid TransactionId,
    int PumpId,
    string Status,
    string State,
    long AmountMinor,
    string Currency,
    string? AuthorisationCode,
    bool Offline,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt)
{
    /// <summary>Projects a journal record to its view.</summary>
    public static TransactionView From(TransactionContext tx) => new(
        tx.TransactionId, tx.PumpId, tx.Status.ToString(), tx.State.ToString(),
        tx.Amount.Minor, tx.Amount.CurrencyCode, tx.AuthorisationCode, tx.Offline, tx.CreatedAt, tx.UpdatedAt);
}

/// <summary>The full site snapshot a reconnecting dashboard resynchronises against.</summary>
/// <param name="Pumps">Every pump's live state.</param>
/// <param name="RecentTransactions">The most recent transactions.</param>
/// <param name="Terminals">Every outdoor payment terminal's screen state.</param>
/// <param name="Faults">The currently armed fault set.</param>
/// <param name="Totals">Site totals at the moment of the snapshot.</param>
public sealed record SiteSnapshot(
    IReadOnlyList<PumpSnapshot> Pumps,
    IReadOnlyList<TransactionView> RecentTransactions,
    IReadOnlyList<Opt.OptView> Terminals,
    Faults.FaultState Faults,
    TotalsView Totals);

/// <summary>One fuel grade as the dashboard shows it.</summary>
/// <param name="Code">Grade code.</param>
/// <param name="Name">Display name.</param>
/// <param name="PricePerLitreMinor">Unit price in minor units per litre.</param>
public sealed record GradeView(string Code, string Name, int PricePerLitreMinor);

/// <summary>Static site configuration the dashboard reads once at start-up.</summary>
/// <param name="Currency">ISO 4217 currency for every amount on the site.</param>
/// <param name="PumpCount">How many pumps the grid should render.</param>
/// <param name="FloorLimitMinor">Per-transaction offline floor limit.</param>
/// <param name="FirmwarePumps">True when the pumps are real firmware processes, not journal-driven state.</param>
/// <param name="Grades">The grades on sale.</param>
/// <param name="Cards">The test cards the card simulator can present.</param>
public sealed record SiteInfoView(
    string Currency,
    int PumpCount,
    long FloorLimitMinor,
    bool FirmwarePumps,
    IReadOnlyList<GradeView> Grades,
    IReadOnlyList<Opt.CardProfileView> Cards);

/// <summary>Totals across the journal.</summary>
/// <param name="Currency">Currency of the totals.</param>
/// <param name="Completed">Count of settled transactions.</param>
/// <param name="Declined">Count of declined transactions.</param>
/// <param name="Reversed">Count of reversed transactions.</param>
/// <param name="OfflinePending">Count of offline authorisations awaiting replay.</param>
/// <param name="CompletedValueMinor">Sum of settled amounts, in minor units.</param>
/// <param name="OfflineExposureMinor">Current offline exposure, in minor units.</param>
public sealed record TotalsView(
    string Currency, int Completed, int Declined, int Reversed, int OfflinePending,
    long CompletedValueMinor, long OfflineExposureMinor)
{
    /// <summary>Rolls a journal read up into site totals.</summary>
    /// <param name="all">Every journal record.</param>
    /// <param name="offlineExposureMinor">Current unreplayed offline exposure.</param>
    /// <param name="currency">Site currency.</param>
    public static TotalsView From(IReadOnlyList<TransactionContext> all, long offlineExposureMinor, string currency)
    {
        ArgumentNullException.ThrowIfNull(all);
        return new TotalsView(
            Currency: currency,
            Completed: all.Count(t => t.Status == TransactionStatus.Completed),
            Declined: all.Count(t => t.Status == TransactionStatus.Declined),
            Reversed: all.Count(t => t.Status == TransactionStatus.Reversed),
            OfflinePending: all.Count(t => t.Offline && t.Status == TransactionStatus.Approved),
            CompletedValueMinor: all.Where(t => t.Status == TransactionStatus.Completed).Sum(t => t.Amount.Minor),
            OfflineExposureMinor: offlineExposureMinor);
    }
}
