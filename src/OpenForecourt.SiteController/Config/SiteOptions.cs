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

    /// <summary>Number of pumps on the forecourt (the demo uses four).</summary>
    public int PumpCount { get; set; } = 4;

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
}
