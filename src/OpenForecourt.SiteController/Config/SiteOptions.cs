namespace OpenForecourt.SiteController.Config;

/// <summary>
/// Site-wide configuration: limits, timeouts and the endpoints that select which port
/// adapter is used. Every timeout here is honoured through <see cref="Abstractions.Ports.IClock"/>
/// so it stays testable (CLAUDE.md section 6).
/// </summary>
public sealed class SiteOptions
{
    /// <summary>Configuration section name.</summary>
    public const string Section = "Site";

    /// <summary>ISO 4217 alphabetic currency for all site amounts.</summary>
    public string Currency { get; set; } = "GBP";

    /// <summary>Number of pumps on the forecourt. The dashboard grid is sized from this.</summary>
    public int PumpCount { get; set; } = 8;

    /// <summary>
    /// Path to the pump firmware host binary (<c>firmware/pump/build-host/pump_host</c>). When set,
    /// the site controller launches one process per pump and drives it over OFP-1, so the grid shows
    /// real metered volume. When null the fleet is not started and pump state stays journal-driven.
    /// </summary>
    public string? PumpFirmwarePath { get; set; }

    /// <summary>First TCP port for the pump fleet; pump <c>n</c> listens on <c>base + n - 1</c>.</summary>
    public int PumpBasePort { get; set; } = 9401;

    /// <summary>Flow-meter tick period handed to the firmware, in milliseconds.</summary>
    public int PumpTickMs { get; set; } = 100;

    /// <summary>Default millilitres delivered per flow-meter tick; the simulator panel overrides it live.</summary>
    public int PumpFlowMlPerTick { get; set; } = 100;

    /// <summary>Directory of virtual card profiles the card simulator offers.</summary>
    public string CardProfilesPath { get; set; } = "cards";

    /// <summary>How long the OPT waits for the customer to enter a PIN before timing out.</summary>
    public TimeSpan PinEntryTimeout { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Path to the hex-encoded DUKPT Base Derivation Key used to encrypt online PIN blocks.
    /// Deliberately null by default: a key path is never a config default in <c>src/</c>, it is
    /// supplied by the demo from <c>tests/testdata/keys</c> (CLAUDE.md section 7.2). With no BDK the
    /// OPT refuses online-PIN cards rather than handling a PIN it cannot protect.
    /// </summary>
    public string? PinBdkPath { get; set; }

    /// <summary>Initial KSN for the OPT's PIN pad; it advances once per card session.</summary>
    public string PinKsn { get; set; } = "FFFF9876543210E00000";

    /// <summary>Fuel grades the forecourt sells, cheapest first.</summary>
    public IList<GradeOption> Grades { get; set; } =
    [
        new() { Code = "U95", Name = "Unleaded 95", PricePerLitreMinor = 149 },
        new() { Code = "U98", Name = "Super Unleaded 98", PricePerLitreMinor = 167 },
        new() { Code = "DSL", Name = "Diesel", PricePerLitreMinor = 158 },
    ];

    /// <summary>Per-transaction ceiling for offline authorisation, in minor units (the floor limit).</summary>
    public long FloorLimitMinor { get; set; } = 5000;

    /// <summary>Total value of unreplayed offline authorisations the site will carry, in minor units.</summary>
    public long OfflineExposureCeilingMinor { get; set; } = 20000;

    /// <summary>How long to wait for a host response before treating it as no response.</summary>
    public TimeSpan HostTimeout { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>How often the availability prober pings the host while it is down.</summary>
    public TimeSpan HostProbeInterval { get; set; } = TimeSpan.FromSeconds(3);

    /// <summary>SQLite journal file path. Use <c>":memory:"</c> only in tests.</summary>
    public string JournalPath { get; set; } = "forecourt-journal.db";

    /// <summary><c>host:port</c> of the acquiring host for the TCP link; null selects the in-process host.</summary>
    public string? HostEndpoint { get; set; }

    /// <summary>
    /// The test PAN placed on the host request. In production a payment gateway detokenizes the
    /// card token at the acquirer boundary; this simulation stands in for that exchange with a
    /// single reserved test PAN (CLAUDE.md section 7.1). Never a real card number.
    /// </summary>
    public string TestPan { get; set; } = "4111111111111111";

    /// <summary>AMQP URI for RabbitMQ dispatch; null selects the in-process dispatch.</summary>
    public string? RabbitMqUri { get; set; }

    /// <summary>
    /// When true an approval is a pre-authorisation: settlement waits for the dispenser to report
    /// the delivered volume. Set automatically when the pump fleet is running; false leaves the
    /// Phase 5 behaviour of settling the authorised amount immediately.
    /// </summary>
    public bool SettleOnDispenseComplete { get; set; }
}

/// <summary>One fuel grade: what the customer selects and what the dispenser charges per litre.</summary>
public sealed class GradeOption
{
    /// <summary>Short grade code shown on the pump tile.</summary>
    public string Code { get; set; } = "";

    /// <summary>Display name.</summary>
    public string Name { get; set; } = "";

    /// <summary>Unit price in minor units per litre, pushed to the firmware on selection.</summary>
    public int PricePerLitreMinor { get; set; }
}
