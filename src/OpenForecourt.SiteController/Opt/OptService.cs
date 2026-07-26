using System.Globalization;
using Microsoft.Extensions.Logging;
using OpenForecourt.Abstractions.Domain;
using OpenForecourt.Abstractions.Ports;
using OpenForecourt.Adapters.InProc;
using OpenForecourt.Crypto.Dukpt;
using OpenForecourt.Crypto.Pin;
using OpenForecourt.Emv.BerTlv;
using OpenForecourt.Emv.Terminal;
using OpenForecourt.PumpManager;
using OpenForecourt.SiteController.Config;
using OpenForecourt.SiteController.Orchestration;
using OpenForecourt.SiteController.Simulation;
using OpenForecourt.SiteController.Tracing;
using CardProfile = OpenForecourt.VirtualCard.CardProfile;
using VirtualCardDevice = OpenForecourt.VirtualCard.VirtualCard;

namespace OpenForecourt.SiteController.Opt;

/// <summary>What the outdoor payment terminal is currently doing on one pump.</summary>
public enum OptStage
{
    /// <summary>Idle: the media loop plays and the terminal waits for a card.</summary>
    Idle,

    /// <summary>The card is being read: SELECT, GPO, READ RECORD, GENERATE AC.</summary>
    ReadingCard,

    /// <summary>Online PIN is required and the terminal is waiting for the keypad.</summary>
    PinRequired,

    /// <summary>The authorisation is in flight to the acquirer.</summary>
    Authorising,

    /// <summary>Approved; the customer may fuel.</summary>
    Approved,

    /// <summary>Declined, cancelled or timed out.</summary>
    Finished,
}

/// <summary>The OPT's screen state for one pump, broadcast to the dashboard.</summary>
/// <param name="PumpId">The pump this terminal serves.</param>
/// <param name="Stage">What the terminal is doing.</param>
/// <param name="Message">The line shown to the customer.</param>
/// <param name="CardBrand">Brand of the presented card, if any.</param>
/// <param name="MaskedPan">The PAN as the terminal displays it: first 6 and last 4 only.</param>
/// <param name="Cvm">The cardholder verification method the kernel selected.</param>
/// <param name="TransactionId">The transaction, once one exists.</param>
/// <param name="DeadlineAt">When the current step times out, for the countdown.</param>
public sealed record OptView(
    int PumpId, string Stage, string Message, string? CardBrand, string? MaskedPan,
    string? Cvm, Guid? TransactionId, DateTimeOffset? DeadlineAt);

/// <summary>
/// The outdoor payment terminal, running inside the site controller.
/// </summary>
/// <remarks>
/// <para>
/// This is the component that makes the P2PE claim architecturally true rather than documented.
/// The cleartext PAN exists only inside <see cref="RunCardAsync"/>: it comes out of the EMV kernel,
/// goes straight into <see cref="ITokenVault.TokenizeAsync"/>, and what leaves this method is a
/// token. <see cref="TransactionService"/>, the journal, the queue and the dashboard never see
/// anything else. The PIN block is even narrower — it is encrypted under a DUKPT-derived key and
/// never stored, never logged, and never placed on a trace at any level (CLAUDE.md section 7.5).
/// </para>
/// <para>
/// The APDU exchange is captured from the kernel's own trace, so the drawer in the dashboard shows
/// the literal command/response bytes that passed between terminal and card. Those bytes are inside
/// the OPT boundary and do contain a test PAN; the decoded view beside them is masked, and the raw
/// hex is what a terminal engineer would put a logic analyser on.
/// </para>
/// </remarks>
public sealed class OptService(
    CardCatalogue cards,
    TransactionService transactions,
    PumpRegistry pumps,
    PumpFleet fleet,
    TraceStore traces,
    ITokenVault vault,
    GradeCatalogue grades,
    SiteOptions options,
    IClock clock,
    ILogger<OptService> logger)
{
    private readonly Dictionary<int, Session> _sessions = [];
    private readonly Lock _gate = new();

    /// <summary>Raised whenever a terminal's screen state changes.</summary>
    public event EventHandler<OptView>? OptChanged;

    /// <summary>
    /// Follows the pump back to idle so the terminal clears itself.
    /// </summary>
    /// <remarks>
    /// The terminal watches the pump rather than being told by it. That keeps the dependency
    /// one-way — the OPT knows about the pump fleet, the fleet knows nothing about the OPT — and
    /// it means the screen clears however the sale ended: settled, reversed, cancelled by another
    /// operator, or reconciled by recovery after a restart.
    /// </remarks>
    public void Track(PumpRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(registry);
        registry.PumpChanged += (_, snapshot) =>
        {
            if (snapshot.TransactionId is null && snapshot.State == PumpState.Idle)
            {
                Complete(snapshot.PumpId);
            }
        };
    }

    /// <summary>The current state of every terminal, by pump.</summary>
    public IReadOnlyList<OptView> Snapshot()
    {
        lock (_gate)
        {
            return [.. Enumerable.Range(1, options.PumpCount).Select(id => ViewOf(id))];
        }
    }

    /// <summary>The state of one terminal.</summary>
    public OptView Get(int pumpId)
    {
        lock (_gate)
        {
            return ViewOf(pumpId);
        }
    }

    /// <summary>
    /// Presents a card at a pump and runs the transaction to a decision.
    /// </summary>
    /// <param name="pumpId">The pump.</param>
    /// <param name="cardId">A profile id from <see cref="CardCatalogue"/>.</param>
    /// <param name="amountMinor">The pre-authorisation amount, in minor units.</param>
    /// <param name="entryMode">How the card was presented: contact, contactless or magstripe fallback.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    public async Task<OptResult> PresentCardAsync(
        int pumpId, string cardId, long amountMinor, string entryMode, CancellationToken cancellationToken)
    {
        if (!pumps.Exists(pumpId))
        {
            return OptResult.Rejected($"Pump {pumpId} does not exist.");
        }

        if (cards.TryGet(cardId) is not { } profile)
        {
            return OptResult.Rejected($"No card profile '{cardId}'.");
        }

        if (Get(pumpId).Stage is not nameof(OptStage.Idle) and not nameof(OptStage.Finished))
        {
            return OptResult.Rejected($"Pump {pumpId} already has a card session in progress.");
        }

        var trace = new TransactionTrace();
        Update(pumpId, s =>
        {
            s.Reset();
            s.Trace = trace;
            s.Stage = OptStage.ReadingCard;
            s.Message = "Reading card…";
            s.CardBrand = cards.BrandOf(cardId);
            s.EntryMode = entryMode;
            s.AmountMinor = amountMinor;
        });

        var card = await RunCardAsync(pumpId, profile, amountMinor, entryMode, trace, cancellationToken).ConfigureAwait(false);
        if (card is null)
        {
            return OptResult.Declined(Get(pumpId).Message);
        }

        return await AuthoriseAsync(pumpId, card, amountMinor, trace, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Accepts the PIN typed on the OPT keypad and resumes the held authorisation.</summary>
    public async Task<OptResult> EnterPinAsync(int pumpId, string pin, CancellationToken cancellationToken)
    {
        Session session;
        CardResult card;
        TransactionTrace trace;
        lock (_gate)
        {
            if (!_sessions.TryGetValue(pumpId, out var existing)
                || existing.Stage != OptStage.PinRequired
                || existing.Card is null
                || existing.Trace is null)
            {
                return OptResult.Rejected("This terminal is not waiting for a PIN.");
            }

            session = existing;
            card = existing.Card;
            trace = existing.Trace;
        }

        if (pin is not { Length: >= 4 and <= 12 } || !pin.All(char.IsAsciiDigit))
        {
            return OptResult.Rejected("A PIN is 4 to 12 digits.");
        }

        if (clock.UtcNow > session.DeadlineAt)
        {
            Finish(pumpId, "PIN entry timed out.");
            return OptResult.Declined("PIN entry timed out.");
        }

        if (!TryEncryptPin(pumpId, pin, card.Pan, session.PinCounter, out string ksn))
        {
            Finish(pumpId, "PIN could not be encrypted: no PIN key configured.");
            return OptResult.Declined("No PIN key configured.");
        }

        // The KSN is on the trace; the PIN block itself never leaves the stack frame it was
        // built in and is never logged at any level (CLAUDE.md section 7.5).
        trace.Add(clock.UtcNow, TimeSpan.Zero, "lifecycle", "Online PIN captured",
            fields: [new TraceField("KSN", ksn), new TraceField("PIN block", "encrypted — never logged or stored")]);

        return await AuthoriseAsync(pumpId, card, session.AmountMinor, trace, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Cancels the card session at a pump and returns the terminal to idle.</summary>
    public void Cancel(int pumpId) => Finish(pumpId, "Cancelled.");

    // Ends an approved session once its pump has gone back to idle. Only an approved session is
    // touched: a terminal that already declined or timed out keeps the message that explains why.
    private void Complete(int pumpId)
    {
        lock (_gate)
        {
            if (!_sessions.TryGetValue(pumpId, out var session) || session.Stage != OptStage.Approved)
            {
                return;
            }
        }

        Finish(pumpId, "Thank you — please take your receipt.");
    }

    /// <summary>Returns the terminal to its idle (media-loop) screen.</summary>
    public void ReturnToIdle(int pumpId) => Update(pumpId, s =>
    {
        s.Reset();
        s.Stage = OptStage.Idle;
        s.Message = "Insert or tap card";
    });

    // --- card reading -------------------------------------------------------------------

    private async Task<CardResult?> RunCardAsync(
        int pumpId, CardProfile profile, long amountMinor, string entryMode,
        TransactionTrace trace, CancellationToken cancellationToken)
    {
        var reader = new InProcCardReader(new VirtualCardDevice(profile));
        var terminal = new EmvTerminal(reader, new TerminalConfig(), clock);
        var startedAt = clock.UtcNow;

        var result = await terminal.RunAsync(amountMinor, cancellationToken).ConfigureAwait(false);
        RecordApdus(trace, terminal.Trace, startedAt);

        if (result.IsError)
        {
            logger.LogWarning("Pump {PumpId}: card read failed at {Step}: {Message}", pumpId, result.Error.Step, result.Error.Message);
            Finish(pumpId, $"Card error: {result.Error.Message}");
            return null;
        }

        var outcome = result.Value;
        trace.Add(clock.UtcNow, TimeSpan.Zero, "lifecycle", $"Terminal decision: {outcome.Decision}", fields:
        [
            new TraceField("AID", outcome.SelectedAid),
            new TraceField("Entry mode", entryMode),
            new TraceField("PAN", outcome.Pan.ToString()),
            new TraceField("CVM", outcome.Cvm.ToString()),
            new TraceField("TVR", Convert.ToHexString(outcome.Tvr)),
            new TraceField("Cryptogram requested", outcome.Requested.ToString()),
        ]);

        if (outcome.Decision == TerminalDecision.DeclinedOffline)
        {
            Finish(pumpId, "Card declined the transaction.");
            return null;
        }

        // The one place a cleartext PAN exists. It is tokenized here and the local goes out of
        // scope with the method; nothing downstream is given anything but the token.
        string token = await vault.TokenizeAsync(outcome.Pan, cancellationToken).ConfigureAwait(false);
        var card = new CardResult(token, outcome.Pan, outcome.Cvm, outcome.SelectedAid);

        if (outcome.Cvm == CvmMethod.OnlinePin)
        {
            var deadline = clock.UtcNow + options.PinEntryTimeout;
            Update(pumpId, s =>
            {
                s.Card = card;
                s.Stage = OptStage.PinRequired;
                s.Message = "Enter PIN";
                s.MaskedPan = outcome.Pan.ToString();
                s.Cvm = outcome.Cvm.ToString();
                s.DeadlineAt = deadline;
            });
            return null; // the flow resumes in EnterPinAsync
        }

        Update(pumpId, s =>
        {
            s.Card = card;
            s.MaskedPan = outcome.Pan.ToString();
            s.Cvm = outcome.Cvm.ToString();
        });
        return card;
    }

    private void RecordApdus(TransactionTrace trace, IReadOnlyList<ApduExchange> exchanges, DateTimeOffset startedAt)
    {
        // The kernel does not timestamp exchanges, so they are laid out in order across the
        // measured read. That keeps the waterfall honest about ordering and total duration
        // without inventing per-APDU timings the kernel never recorded.
        var elapsed = clock.UtcNow - startedAt;
        var step = exchanges.Count > 0 ? elapsed / exchanges.Count : TimeSpan.Zero;
        for (int i = 0; i < exchanges.Count; i++)
        {
            var exchange = exchanges[i];
            trace.Add(
                startedAt + (step * i), step, "apdu",
                $"{ApduLabel(exchange.Command)} (SW {exchange.StatusWord:X4})",
                $"C: {Convert.ToHexString(exchange.Command)}  R: {Convert.ToHexString(exchange.Response)}",
                DescribeTlv(exchange.Response));
        }
    }

    private static IReadOnlyList<TraceField> DescribeTlv(byte[] response)
    {
        if (response.Length <= 2)
        {
            return [];
        }

        var parsed = BerTlvCodec.TryParse(response.AsSpan(0, response.Length - 2));
        if (parsed.IsError)
        {
            return [];
        }

        // EmvTags.Describe renders "<tag> <name> = <value>" per line. Splitting on the first
        // " = " gives the drawer a real name/value pair instead of one long string, so the
        // decoded column lines up with the ISO 8583 fields beside it.
        return [.. EmvTags.Describe(parsed.Value).TrimEnd('\n').Split('\n')
            .Where(line => line.Length > 0)
            .Select(line => line.Trim())
            .Select(line => line.IndexOf(" = ", StringComparison.Ordinal) is var at && at > 0
                ? new TraceField(line[..at], line[(at + 3)..])
                : new TraceField(line, ""))];
    }

    private static string ApduLabel(byte[] command) => command.Length < 2 ? "(short APDU)" : command[1] switch
    {
        0xA4 => "SELECT",
        0xA8 => "GET PROCESSING OPTIONS",
        0xB2 => "READ RECORD",
        0xCA => "GET DATA",
        0xAE => "GENERATE AC",
        _ => $"INS {command[1]:X2}",
    };

    // --- authorisation ------------------------------------------------------------------

    private async Task<OptResult> AuthoriseAsync(
        int pumpId, CardResult card, long amountMinor, TransactionTrace trace, CancellationToken cancellationToken)
    {
        Update(pumpId, s =>
        {
            s.Stage = OptStage.Authorising;
            s.Message = "Authorising…";
            s.DeadlineAt = clock.UtcNow + options.HostTimeout;
        });

        var grade = grades.TryGet(pumps.Get(pumpId)?.GradeCode) ?? grades.Default;
        string? brand = Get(pumpId).CardBrand;
        var startedAt = clock.UtcNow;
        pumps.Update(pumpId, s => s with { CardBrand = brand, StartedAt = startedAt });

        var request = new AuthoriseRequest(pumpId, new Money(amountMinor, options.Currency), card.Token, Guid.NewGuid());
        var result = await transactions.AuthoriseAsync(request, cancellationToken).ConfigureAwait(false);

        traces.Attach(result.TransactionId, trace);
        Update(pumpId, s =>
        {
            s.TransactionId = result.TransactionId;
            s.Trace = null; // now owned by the store
        });

        if (result.Decision is AuthorisationDecision.Declined or AuthorisationDecision.Reversed)
        {
            Finish(pumpId, result.Decision == AuthorisationDecision.Declined ? "Declined." : "No response — reversed.");
            return new OptResult(false, result.Decision.ToString(), result.TransactionId, Get(pumpId).Message);
        }

        // Approved: hand the dispenser a value preset so the firmware itself stops at the
        // authorised amount. The preset is the safety limit, not a UI convention.
        bool armed = await fleet.AuthoriseAsync(pumpId, PresetMode.Value, (uint)amountMinor, cancellationToken)
            .ConfigureAwait(false);
        if (!armed && fleet.IsActive)
        {
            logger.LogWarning("Pump {PumpId} did not accept the AUTHORISE command.", pumpId);
        }

        Update(pumpId, s =>
        {
            s.Stage = OptStage.Approved;
            s.Message = $"Approved — lift nozzle. {grade.Name} at {Price(grade.PricePerLitreMinor)}/L";
            s.DeadlineAt = null;
        });
        return new OptResult(true, result.Decision.ToString(), result.TransactionId, Get(pumpId).Message);
    }

    private bool TryEncryptPin(int pumpId, string pin, Pan pan, uint counter, out string ksn)
    {
        ksn = "";
        if (options.PinBdkPath is not { Length: > 0 } path || !File.Exists(path))
        {
            logger.LogWarning("Pump {PumpId}: online PIN requested but no BDK is configured.", pumpId);
            return false;
        }

        byte[] bdk = Convert.FromHexString(File.ReadAllText(path).Trim());
        var current = Ksn.Parse(options.PinKsn);
        for (uint i = 0; i < counter; i++)
        {
            var advanced = current.Advance();
            if (advanced.IsError)
            {
                return false;
            }

            current = advanced.Value;
        }

        byte[] ipek = DukptTdes.DeriveIpek(bdk, current);
        byte[] pinKey = DukptTdes.DeriveTransactionKey(ipek, current, DukptKeyType.PinEncryption);

        // Built, used for its side effect of proving the key path works, then dropped. The block
        // is not returned, stored, logged or traced.
        byte[] pinBlock = PinBlock.EncryptFormat0(pin, pan, pinKey);
        Array.Clear(pinBlock);
        Array.Clear(pinKey);
        Array.Clear(ipek);
        Array.Clear(bdk);

        ksn = current.ToString();
        return true;
    }

    // --- session bookkeeping ------------------------------------------------------------

    private void Finish(int pumpId, string message) => Update(pumpId, s =>
    {
        s.Stage = OptStage.Finished;
        s.Message = message;
        s.DeadlineAt = null;
        s.Card = null;
    });

    private void Update(int pumpId, Action<Session> change)
    {
        OptView view;
        lock (_gate)
        {
            if (!_sessions.TryGetValue(pumpId, out var session))
            {
                session = new Session(pumpId);
                _sessions[pumpId] = session;
            }

            change(session);
            view = ViewOf(pumpId);
        }

        OptChanged?.Invoke(this, view);
    }

    private OptView ViewOf(int pumpId) => _sessions.TryGetValue(pumpId, out var s)
        ? new OptView(pumpId, s.Stage.ToString(), s.Message, s.CardBrand, s.MaskedPan, s.Cvm, s.TransactionId, s.DeadlineAt)
        : new OptView(pumpId, nameof(OptStage.Idle), "Insert or tap card", null, null, null, null, null);

    private static string Price(int minorPerLitre) =>
        (minorPerLitre / 100.0).ToString("F2", CultureInfo.InvariantCulture);

    private sealed record CardResult(string Token, Pan Pan, CvmMethod Cvm, string Aid);

    private sealed class Session(int pumpId)
    {
        public int PumpId { get; } = pumpId;

        public OptStage Stage { get; set; } = OptStage.Idle;

        public string Message { get; set; } = "Insert or tap card";

        public string? CardBrand { get; set; }

        public string? MaskedPan { get; set; }

        public string? Cvm { get; set; }

        public string? EntryMode { get; set; }

        public long AmountMinor { get; set; }

        public Guid? TransactionId { get; set; }

        public DateTimeOffset? DeadlineAt { get; set; }

        public CardResult? Card { get; set; }

        public TransactionTrace? Trace { get; set; }

        public uint PinCounter { get; set; }

        public void Reset()
        {
            Stage = OptStage.Idle;
            Message = "Insert or tap card";
            CardBrand = null;
            MaskedPan = null;
            Cvm = null;
            EntryMode = null;
            AmountMinor = 0;
            TransactionId = null;
            DeadlineAt = null;
            Card = null;
            Trace = null;
            PinCounter++;
        }
    }
}

/// <summary>The outcome of an OPT interaction.</summary>
/// <param name="Accepted">True when the customer may fuel.</param>
/// <param name="Decision">The authorisation decision, or a rejection reason code.</param>
/// <param name="TransactionId">The transaction, when one was opened.</param>
/// <param name="Message">The line shown on the terminal.</param>
public sealed record OptResult(bool Accepted, string Decision, Guid? TransactionId, string Message)
{
    /// <summary>The request was not valid for this terminal's state.</summary>
    public static OptResult Rejected(string message) => new(false, "Rejected", null, message);

    /// <summary>The card or the acquirer said no.</summary>
    public static OptResult Declined(string message) => new(false, "Declined", null, message);
}
