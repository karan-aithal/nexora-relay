using Microsoft.Extensions.Logging.Abstractions;
using OpenForecourt.Abstractions.Ports;
using OpenForecourt.Adapters.InProc;
using OpenForecourt.SiteController.Config;
using OpenForecourt.SiteController.Orchestration;

namespace OpenForecourt.SiteController.UnitTests;

/// <summary>
/// Wires a <see cref="TransactionService"/> over the in-process ports for orchestration tests,
/// exposing the ports so a test can flip host availability, inspect the journal and assert
/// dispatch effects.
/// </summary>
internal sealed class SiteHarness
{
    public SiteHarness(ITransactionJournal journal, SiteOptions? options = null)
    {
        Journal = journal;
        Options = options ?? new SiteOptions
        {
            Currency = "GBP",
            PumpCount = 8,
            FloorLimitMinor = 5000,
            OfflineExposureCeilingMinor = 1_000_000,
        };
        Offline = new OfflinePolicy(Options);
        Pumps = new PumpRegistry(Options);
        Service = new TransactionService(
            journal, Host, Dispatch, Availability, Offline, Pumps, new StanSequence(),
            Clock, Options, NullLogger<TransactionService>.Instance);
    }

    public InProcHostConnection Host { get; } = new();

    public InProcDispatch Dispatch { get; } = new();

    public ITransactionJournal Journal { get; }

    public HostAvailability Availability { get; } = new();

    public OfflinePolicy Offline { get; }

    public PumpRegistry Pumps { get; }

    public SiteOptions Options { get; }

    public FakeClock Clock { get; } = new(new DateTimeOffset(2026, 7, 25, 12, 0, 0, TimeSpan.Zero));

    public TransactionService Service { get; }

    public AuthoriseRequest Request(int pumpId, long amountMinor) =>
        new(pumpId, new Abstractions.Domain.Money(amountMinor, Options.Currency), $"TOK-{pumpId}", Guid.NewGuid());
}
