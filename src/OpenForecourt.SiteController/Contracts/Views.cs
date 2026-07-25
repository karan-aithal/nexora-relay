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
public sealed record SiteSnapshot(IReadOnlyList<PumpSnapshot> Pumps, IReadOnlyList<TransactionView> RecentTransactions);

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
    long CompletedValueMinor, long OfflineExposureMinor);
