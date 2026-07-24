using System.Buffers;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using OpenForecourt.Abstractions;
using OpenForecourt.Abstractions.Domain;
using OpenForecourt.Abstractions.Ports;
using OpenForecourt.Emv.BerTlv;

namespace OpenForecourt.Emv.Terminal;

/// <summary>
/// An EMV terminal kernel (online-authorisation subset) driven as an explicit state machine.
/// It runs the standard flow against an <see cref="ICardReader"/>: build the candidate list,
/// select the application, GET PROCESSING OPTIONS, read the application data, apply
/// processing restrictions, run terminal risk management, select a CVM, do terminal action
/// analysis, ask the card for a first GENERATE AC, and assemble ISO 8583 field 55.
/// </summary>
/// <remarks>
/// <para>
/// Scope is online (ARQC) only, per CLAUDE.md section 5. Every card exchange is recorded in
/// <see cref="Trace"/> so a transaction can be printed step by step (the phase-2 demo) or
/// pinned by a golden test. Card and reader errors are returned as
/// <see cref="Result{TValue,TError}"/>, never thrown (CLAUDE.md section 6).
/// </para>
/// <para>
/// The unpredictable number (tag 9F37) is normally random; a fixed value can be supplied so
/// a golden APDU trace is reproducible.
/// </para>
/// </remarks>
public sealed class EmvTerminal(
    ICardReader reader,
    TerminalConfig config,
    IClock clock,
    byte[]? unpredictableNumber = null)
{
    private readonly List<ApduExchange> _trace = [];
    private readonly TagStore _data = new();
    private readonly Tvr _tvr = new();

    /// <summary>Every APDU exchanged with the card, in order.</summary>
    public IReadOnlyList<ApduExchange> Trace => _trace;

    /// <summary>Runs the full transaction flow for the given amount (minor units).</summary>
    public async Task<Result<TerminalOutcome, TerminalError>> RunAsync(long amountMinor, CancellationToken cancellationToken)
    {
        SeedTerminalData(amountMinor);

        var present = await reader.WaitForCardAsync(cancellationToken).ConfigureAwait(false);
        if (present.IsError)
        {
            return Fail<TerminalOutcome>("card-detection", $"no card presented: {present.Error}.");
        }

        var candidates = await BuildCandidateListAsync(cancellationToken).ConfigureAwait(false);
        if (candidates.IsError)
        {
            return Result<TerminalOutcome, TerminalError>.Fail(candidates.Error);
        }

        var selected = await SelectApplicationAsync(candidates.Value, cancellationToken).ConfigureAwait(false);
        if (selected.IsError)
        {
            return Result<TerminalOutcome, TerminalError>.Fail(selected.Error);
        }

        var gpo = await GetProcessingOptionsAsync(selected.Value.Pdol, cancellationToken).ConfigureAwait(false);
        if (gpo.IsError)
        {
            return Result<TerminalOutcome, TerminalError>.Fail(gpo.Error);
        }

        var read = await ReadApplicationDataAsync(gpo.Value, cancellationToken).ConfigureAwait(false);
        if (read.IsError)
        {
            return Result<TerminalOutcome, TerminalError>.Fail(read.Error);
        }

        ProcessingRestrictions();
        await TerminalRiskManagementAsync(amountMinor, cancellationToken).ConfigureAwait(false);
        var cvm = SelectCvm();
        var requested = TerminalActionAnalysis();

        var ac = await GenerateAcAsync(requested, cancellationToken).ConfigureAwait(false);
        if (ac.IsError)
        {
            return Result<TerminalOutcome, TerminalError>.Fail(ac.Error);
        }

        return AssembleOutcome(selected.Value.Aid, requested, cvm);
    }

    // ---- Step 0: terminal-resident data ---------------------------------------------------

    private void SeedTerminalData(long amountMinor)
    {
        _data.Set("9F1A", Hex(config.CountryCode));
        _data.Set("5F2A", Hex(config.CurrencyCode));
        _data.Set("9F35", Hex(config.TerminalType));
        _data.Set("9F33", Hex(config.TerminalCapabilities));
        _data.Set("9F1E", Hex(config.IfdSerialNumber));
        _data.Set("9C", Hex(config.TransactionType));
        _data.Set("9F02", BcdAmount(amountMinor));
        _data.Set("9F03", BcdAmount(0));
        _data.Set("9F37", unpredictableNumber ?? RandomNumberGenerator.GetBytes(4));
        _data.Set("9A", BcdDate(clock.UtcNow));
    }

    // ---- Step 1: candidate list -----------------------------------------------------------

    private readonly record struct Candidate(byte[] Aid, int Priority);

    private async Task<Result<IReadOnlyList<Candidate>, TerminalError>> BuildCandidateListAsync(CancellationToken ct)
    {
        // Try the PPSE (contactless-style directory) first, then fall back to the PSE.
        var ppse = await SelectByNameAsync("2PAY.SYS.DDF01", ct).ConfigureAwait(false);
        if (ppse.IsError)
        {
            return Result<IReadOnlyList<Candidate>, TerminalError>.Fail(ppse.Error);
        }

        if (StatusOk(ppse.Value))
        {
            return ExtractCandidates(Parse(Body(ppse.Value)), "PPSE");
        }

        return await BuildCandidateListFromPseAsync(ct).ConfigureAwait(false);
    }

    private async Task<Result<IReadOnlyList<Candidate>, TerminalError>> BuildCandidateListFromPseAsync(CancellationToken ct)
    {
        var pse = await SelectByNameAsync("1PAY.SYS.DDF01", ct).ConfigureAwait(false);
        if (pse.IsError)
        {
            return Result<IReadOnlyList<Candidate>, TerminalError>.Fail(pse.Error);
        }

        if (!StatusOk(pse.Value))
        {
            return Fail<IReadOnlyList<Candidate>>("candidate-list", "neither PPSE nor PSE could be selected.");
        }

        var fci = Parse(Body(pse.Value));
        var sfiNode = FindAll(fci, Tag.Parse("88")).FirstOrDefault();
        if (sfiNode is null)
        {
            return Fail<IReadOnlyList<Candidate>>("candidate-list", "PSE FCI has no directory SFI (tag 88).");
        }

        int sfi = sfiNode.Value.Span[0];
        var record = await ReadRecordAsync(sfi, 1, ct).ConfigureAwait(false);
        if (record.IsError)
        {
            return Result<IReadOnlyList<Candidate>, TerminalError>.Fail(record.Error);
        }

        if (!StatusOk(record.Value))
        {
            return Fail<IReadOnlyList<Candidate>>("candidate-list", "PSE directory record 1 could not be read.");
        }

        return ExtractCandidates(Parse(Body(record.Value)), "PSE");
    }

    private static Result<IReadOnlyList<Candidate>, TerminalError> ExtractCandidates(
        IReadOnlyList<TlvNode> nodes, string source)
    {
        var candidates = new List<Candidate>();
        foreach (var entry in FindAll(nodes, Tag.Parse("61")))
        {
            var aid = entry.Find(Tag.Parse("4F"));
            if (aid is null)
            {
                continue;
            }

            var priority = entry.Find(Tag.Parse("87"));
            int p = priority is not null && priority.Value.Length > 0 ? priority.Value.Span[0] & 0x0F : 15;
            candidates.Add(new Candidate(aid.Value.ToArray(), p));
        }

        return candidates.Count == 0
            ? Fail<IReadOnlyList<Candidate>>("candidate-list", $"{source} listed no applications.")
            : Result<IReadOnlyList<Candidate>, TerminalError>.Ok(candidates);
    }

    // ---- Step 2: application selection -----------------------------------------------------

    private readonly record struct Selected(byte[] Aid, byte[] Pdol);

    private async Task<Result<Selected, TerminalError>> SelectApplicationAsync(
        IReadOnlyList<Candidate> candidates, CancellationToken ct)
    {
        // Highest priority is the lowest priority number.
        var chosen = candidates.OrderBy(c => c.Priority).First();

        var resp = await SelectByAidAsync(chosen.Aid, ct).ConfigureAwait(false);
        if (resp.IsError)
        {
            return Result<Selected, TerminalError>.Fail(resp.Error);
        }

        if (!StatusOk(resp.Value))
        {
            return Fail<Selected>("application-selection", $"SELECT of AID {Convert.ToHexString(chosen.Aid)} returned {Sw(resp.Value):X4}.");
        }

        var fci = Parse(Body(resp.Value));
        _data.Absorb(fci);
        var pdol = FindAll(fci, Tag.Parse("9F38")).FirstOrDefault()?.Value.ToArray() ?? [];
        return Result<Selected, TerminalError>.Ok(new Selected(chosen.Aid, pdol));
    }

    // ---- Step 3: GET PROCESSING OPTIONS ---------------------------------------------------

    private async Task<Result<byte[], TerminalError>> GetProcessingOptionsAsync(byte[] pdol, CancellationToken ct)
    {
        byte[] pdolData = BuildDolData(pdol);
        byte[] commandData = new TlvNodeData("83", pdolData).ToBytes();
        byte[] apdu = [0x80, 0xA8, 0x00, 0x00, (byte)commandData.Length, .. commandData, 0x00];

        var resp = await SendAsync(apdu, "get-processing-options", ct).ConfigureAwait(false);
        if (resp.IsError)
        {
            return resp;
        }

        if (!StatusOk(resp.Value))
        {
            return Fail<byte[]>("get-processing-options", $"GPO returned {Sw(resp.Value):X4}.");
        }

        // Response is either format 1 (tag 80: AIP||AFL) or format 2 (tag 77: TLVs).
        var body = Body(resp.Value);
        var nodes = Parse(body);
        var fmt1 = nodes.FirstOrDefault(n => n.Tag.Value == 0x80);
        byte[] afl;
        if (fmt1 is not null)
        {
            var v = fmt1.Value.Span;
            _data.Set("82", v[..2].ToArray());
            afl = v[2..].ToArray();
        }
        else
        {
            _data.Absorb(nodes);
            afl = _data.Get("94") ?? [];
        }

        return Result<byte[], TerminalError>.Ok(afl);
    }

    // ---- Step 4: read application data ----------------------------------------------------

    private async Task<Result<bool, TerminalError>> ReadApplicationDataAsync(byte[] afl, CancellationToken ct)
    {
        // The AFL is a sequence of 4-byte entries: (SFI<<3) | first record | last record | #auth.
        for (int i = 0; i + 4 <= afl.Length; i += 4)
        {
            int sfi = afl[i] >> 3;
            int first = afl[i + 1];
            int last = afl[i + 2];
            for (int rec = first; rec <= last; rec++)
            {
                var resp = await ReadRecordAsync(sfi, rec, ct).ConfigureAwait(false);
                if (resp.IsError)
                {
                    return Result<bool, TerminalError>.Fail(resp.Error);
                }

                if (!StatusOk(resp.Value))
                {
                    return Fail<bool>("read-application-data", $"READ RECORD SFI {sfi} record {rec} returned {Sw(resp.Value):X4}.");
                }

                _data.Absorb(Parse(Body(resp.Value)));
            }
        }

        return Result<bool, TerminalError>.Ok(true);
    }

    // ---- Step 5: processing restrictions --------------------------------------------------

    private void ProcessingRestrictions()
    {
        // This kernel is online-only (CLAUDE.md section 5): it performs no offline data
        // authentication, so the corresponding TVR bit is always set. Combined with the
        // terminal/issuer "online" action codes, this drives every transaction online.
        _tvr.OfflineDataAuthNotPerformed();

        var txnDate = clock.UtcNow;

        if (_data.Get("5F24") is { } expiry && IsBefore(expiry, txnDate))
        {
            _tvr.ExpiredApplication();
        }

        if (_data.Get("5F25") is { } effective && IsAfter(effective, txnDate))
        {
            _tvr.ApplicationNotYetEffective();
        }

        // AUC (tag 9F07) byte 1 bit 7 = "valid at terminals other than ATMs". This is a fuel
        // forecourt terminal, so that bit must be set or the service is not allowed.
        // SPEC-UNVERIFIED: exact AUC bit layout taken from EMV Book 3 Annex; see walkthrough.
        if (_data.Get("9F07") is { Length: >= 1 } auc && (auc[0] & 0x40) == 0)
        {
            _tvr.RequestedServiceNotAllowed();
        }
    }

    // ---- Step 6: terminal risk management -------------------------------------------------

    private async Task TerminalRiskManagementAsync(long amountMinor, CancellationToken ct)
    {
        // Velocity data: read the ATC and last-online ATC via GET DATA (exercises that path).
        await TryGetDataAsync("9F36", ct).ConfigureAwait(false);
        await TryGetDataAsync("9F13", ct).ConfigureAwait(false);

        if (amountMinor > config.FloorLimitMinor)
        {
            _tvr.ExceedsFloorLimit();
        }
        else if (config.RandomSelectionTargetPercent > 0
                 && RandomNumberGenerator.GetInt32(100) < config.RandomSelectionTargetPercent)
        {
            _tvr.SelectedRandomlyForOnline();
        }
    }

    private async Task TryGetDataAsync(string tagHex, CancellationToken ct)
    {
        var tag = Tag.Parse(tagHex);
        byte[] apdu = [0x80, 0xCA, .. tag.Bytes, 0x00];
        var resp = await SendAsync(apdu, "terminal-risk-management", ct).ConfigureAwait(false);
        if (resp.IsSuccess && StatusOk(resp.Value))
        {
            _data.Absorb(Parse(Body(resp.Value)));
        }
    }

    // ---- Step 7: cardholder verification --------------------------------------------------

    private CvmMethod SelectCvm()
    {
        // AIP (tag 82) byte 1 bit 5 = "cardholder verification is supported".
        var aip = _data.Get("82");
        bool cvSupported = aip is { Length: >= 1 } && (aip[0] & 0x10) != 0;
        if (!cvSupported)
        {
            _data.Set("9F34", [0x1F, 0x00, 0x02]); // no CVM performed, successful
            return CvmMethod.NoCvm;
        }

        var cvmList = _data.Get("8E");
        if (cvmList is null || cvmList.Length < 10)
        {
            _data.Set("9F34", [0x3F, 0x00, 0x01]);
            _tvr.CardholderVerificationFailed();
            return CvmMethod.Failed;
        }

        // Skip amount X (4) and amount Y (4); the rest is a list of 2-byte CV rules.
        for (int i = 8; i + 2 <= cvmList.Length; i += 2)
        {
            byte method = (byte)(cvmList[i] & 0x3F);
            byte condition = cvmList[i + 1];
            bool applyNextIfFailed = (cvmList[i] & 0x40) != 0;

            switch (method)
            {
                case 0x02: // enciphered PIN verified online
                    _tvr.OnlinePinEntered();
                    _data.Set("9F34", [method, condition, 0x00]); // result unknown until issuer verifies
                    return CvmMethod.OnlinePin;
                case 0x1E: // signature
                    _data.Set("9F34", [method, condition, 0x00]);
                    return CvmMethod.Signature;
                case 0x1F: // no CVM required
                    _data.Set("9F34", [method, condition, 0x02]);
                    return CvmMethod.NoCvm;
                default:
                    if (!applyNextIfFailed)
                    {
                        _tvr.CardholderVerificationFailed();
                        _data.Set("9F34", [0x3F, condition, 0x01]);
                        return CvmMethod.Failed;
                    }

                    break; // try the next rule
            }
        }

        _tvr.CardholderVerificationFailed();
        _data.Set("9F34", [0x3F, 0x00, 0x01]);
        return CvmMethod.Failed;
    }

    // ---- Step 7b: terminal action analysis ------------------------------------------------

    private CryptogramType TerminalActionAnalysis()
    {
        byte[] tvr = _tvr.ToBytes();
        _data.Set("95", tvr);

        byte[] iacDenial = _data.Get("9F0E") ?? new byte[5];
        byte[] iacOnline = _data.Get("9F0F") ?? new byte[5];

        if (AnyBitSet(tvr, Hex(config.TacDenial)) || AnyBitSet(tvr, iacDenial))
        {
            return CryptogramType.Aac;
        }

        if (AnyBitSet(tvr, Hex(config.TacOnline)) || AnyBitSet(tvr, iacOnline))
        {
            return CryptogramType.Arqc;
        }

        return CryptogramType.Tc;
    }

    // ---- Step 8: first GENERATE AC --------------------------------------------------------

    private async Task<Result<bool, TerminalError>> GenerateAcAsync(CryptogramType requested, CancellationToken ct)
    {
        byte p1 = requested switch
        {
            CryptogramType.Arqc => 0x80,
            CryptogramType.Tc => 0x40,
            _ => 0x00,
        };

        byte[] cdol1 = _data.Get("8C") ?? [];
        byte[] cdol1Data = BuildDolData(cdol1);
        byte[] apdu = [0x80, 0xAE, p1, 0x00, (byte)cdol1Data.Length, .. cdol1Data, 0x00];

        var resp = await SendAsync(apdu, "generate-ac", ct).ConfigureAwait(false);
        if (resp.IsError)
        {
            return Result<bool, TerminalError>.Fail(resp.Error);
        }

        if (!StatusOk(resp.Value))
        {
            return Fail<bool>("generate-ac", $"GENERATE AC returned {Sw(resp.Value):X4}.");
        }

        var body = Body(resp.Value);
        var nodes = Parse(body);
        var fmt1 = nodes.FirstOrDefault(n => n.Tag.Value == 0x80);
        if (fmt1 is not null)
        {
            // Format 1: 9F27 || 9F36 || 9F26 || 9F10 concatenated in the tag-80 value.
            var v = fmt1.Value;
            _data.Set("9F27", v.Slice(0, 1).ToArray());
            _data.Set("9F36", v.Slice(1, 2).ToArray());
            _data.Set("9F26", v.Slice(3, 8).ToArray());
            if (v.Length > 11)
            {
                _data.Set("9F10", v[11..].ToArray());
            }
        }
        else
        {
            _data.Absorb(nodes);
        }

        return _data.Get("9F27") is not null
            ? Result<bool, TerminalError>.Ok(true)
            : Fail<bool>("generate-ac", "GENERATE AC response carried no CID (tag 9F27).");
    }

    // ---- Step 9: assemble outcome + field 55 ----------------------------------------------

    private Result<TerminalOutcome, TerminalError> AssembleOutcome(byte[] aid, CryptogramType requested, CvmMethod cvm)
    {
        byte cid = (_data.Get("9F27") ?? [0x00])[0];
        var decision = (cid & 0xC0) switch
        {
            0x80 => TerminalDecision.OnlineAuthorisationRequested,
            0x40 => TerminalDecision.ApprovedOffline,
            _ => TerminalDecision.DeclinedOffline,
        };

        var panResult = ExtractPan();
        if (panResult.IsError)
        {
            return Result<TerminalOutcome, TerminalError>.Fail(panResult.Error);
        }

        return Result<TerminalOutcome, TerminalError>.Ok(new TerminalOutcome
        {
            SelectedAid = Convert.ToHexString(aid),
            Pan = panResult.Value,
            Requested = requested,
            Cvm = cvm,
            Decision = decision,
            Tvr = _data.Get("95") ?? new byte[5],
            Field55 = BuildField55(),
        });
    }

    /// <summary>
    /// The tags an acquirer requires in field 55 for an online authorisation. Standard DE55
    /// content: the cryptogram and its context. Absent tags are skipped.
    /// </summary>
    private static readonly string[] Field55Tags =
    [
        "9F26", "9F27", "9F10", "9F37", "9F36", "95", "9A", "9C",
        "9F02", "5F2A", "82", "9F1A", "9F03", "9F33", "9F34", "9F35", "9F1E", "84",
    ];

    private byte[] BuildField55()
    {
        var writer = new ArrayBufferWriter<byte>();
        foreach (var tagHex in Field55Tags)
        {
            if (_data.Get(tagHex) is { } value)
            {
                TlvNode.Primitive(Tag.Parse(tagHex), value).WriteTo(writer);
            }
        }

        return writer.WrittenSpan.ToArray();
    }

    private Result<Pan, TerminalError> ExtractPan()
    {
        // Prefer tag 5A (Application PAN); fall back to the PAN embedded in track 2 (tag 57).
        if (_data.Get("5A") is { } pan5A)
        {
            return Result<Pan, TerminalError>.Ok(new Pan(TrimPanPadding(Convert.ToHexString(pan5A))));
        }

        if (_data.Get("57") is { } track2)
        {
            string hex = Convert.ToHexString(track2);
            int sep = hex.IndexOf('D', StringComparison.OrdinalIgnoreCase);
            if (sep > 0)
            {
                return Result<Pan, TerminalError>.Ok(new Pan(hex[..sep]));
            }
        }

        return Fail<Pan>("assemble", "no PAN found (neither tag 5A nor tag 57).");
    }

    // ---- APDU plumbing --------------------------------------------------------------------

    private Task<Result<byte[], TerminalError>> SelectByNameAsync(string name, CancellationToken ct)
    {
        byte[] df = Encoding.ASCII.GetBytes(name);
        byte[] apdu = [0x00, 0xA4, 0x04, 0x00, (byte)df.Length, .. df, 0x00];
        return SendAsync(apdu, "select", ct);
    }

    private Task<Result<byte[], TerminalError>> SelectByAidAsync(byte[] aid, CancellationToken ct)
    {
        byte[] apdu = [0x00, 0xA4, 0x04, 0x00, (byte)aid.Length, .. aid, 0x00];
        return SendAsync(apdu, "select", ct);
    }

    private Task<Result<byte[], TerminalError>> ReadRecordAsync(int sfi, int record, CancellationToken ct)
    {
        byte p2 = (byte)((sfi << 3) | 0x04);
        byte[] apdu = [0x00, 0xB2, (byte)record, p2, 0x00];
        return SendAsync(apdu, "read-record", ct);
    }

    private async Task<Result<byte[], TerminalError>> SendAsync(byte[] apdu, string step, CancellationToken ct)
    {
        var r = await reader.TransmitAsync(apdu, ct).ConfigureAwait(false);
        if (r.IsError)
        {
            return Fail<byte[]>(step, $"card reader error: {r.Error}.");
        }

        byte[] resp = r.Value.Data.ToArray();
        _trace.Add(new ApduExchange(apdu, resp));
        return resp.Length < 2
            ? Fail<byte[]>(step, "response shorter than a status word.")
            : Result<byte[], TerminalError>.Ok(resp);
    }

    // ---- DOL building ---------------------------------------------------------------------

    private byte[] BuildDolData(byte[] dol)
    {
        var writer = new ArrayBufferWriter<byte>();
        int pos = 0;
        while (pos < dol.Length)
        {
            if (!Tag.TryRead(dol.AsSpan(pos), out var tag, out int tagLen))
            {
                break;
            }

            pos += tagLen;
            if (pos >= dol.Length)
            {
                break;
            }

            int length = dol[pos++];
            byte[] value = _data.TryGet(tag, out var v) ? v : [];
            writer.Write(FitToLength(value, length));
        }

        return writer.WrittenSpan.ToArray();
    }

    private static byte[] FitToLength(byte[] value, int length)
    {
        if (value.Length == length)
        {
            return value;
        }

        var result = new byte[length];
        if (value.Length > length)
        {
            // Too long: keep the rightmost bytes (numeric fields are right-justified).
            value.AsSpan(value.Length - length).CopyTo(result);
        }
        else
        {
            // Too short: right-justify into a zero-filled field.
            value.CopyTo(result.AsSpan(length - value.Length));
        }

        return result;
    }

    // ---- small helpers --------------------------------------------------------------------

    private static IReadOnlyList<TlvNode> Parse(ReadOnlySpan<byte> body)
    {
        var result = BerTlvCodec.TryParse(body);
        return result.IsSuccess ? result.Value : [];
    }

    private static ReadOnlySpan<byte> Body(byte[] response) => response.AsSpan(0, response.Length - 2);

    private static ushort Sw(byte[] response) => (ushort)((response[^2] << 8) | response[^1]);

    private static bool StatusOk(byte[] response) => Sw(response) == 0x9000;

    private static IEnumerable<TlvNode> FindAll(IReadOnlyList<TlvNode> nodes, Tag tag)
    {
        foreach (var node in nodes)
        {
            if (node.Tag == tag)
            {
                yield return node;
            }

            if (node.Tag.IsConstructed)
            {
                foreach (var child in FindAll(node.Children, tag))
                {
                    yield return child;
                }
            }
        }
    }

    private static bool AnyBitSet(byte[] tvr, byte[] mask)
    {
        int n = Math.Min(tvr.Length, mask.Length);
        for (int i = 0; i < n; i++)
        {
            if ((tvr[i] & mask[i]) != 0)
            {
                return true;
            }
        }

        return false;
    }

    private static byte[] Hex(string hex) => Convert.FromHexString(hex);

    private static byte[] BcdAmount(long minor)
    {
        string digits = minor.ToString("D12", CultureInfo.InvariantCulture);
        return Convert.FromHexString(digits);
    }

    private static byte[] BcdDate(DateTimeOffset when) =>
        Convert.FromHexString(when.ToString("yyMMdd", CultureInfo.InvariantCulture));

    private static bool IsBefore(byte[] yymmdd, DateTimeOffset when) => CompareBcdDate(yymmdd, when) < 0;

    private static bool IsAfter(byte[] yymmdd, DateTimeOffset when) => CompareBcdDate(yymmdd, when) > 0;

    /// <summary>Compares a YYMMDD BCD date (20YY assumed) against a moment; sign is date - when.</summary>
    private static int CompareBcdDate(byte[] yymmdd, DateTimeOffset when)
    {
        if (yymmdd.Length < 3)
        {
            return 0;
        }

        string s = Convert.ToHexString(yymmdd);
        int year = 2000 + int.Parse(s.AsSpan(0, 2), NumberStyles.None, CultureInfo.InvariantCulture);
        int month = int.Parse(s.AsSpan(2, 2), NumberStyles.None, CultureInfo.InvariantCulture);
        int day = int.Parse(s.AsSpan(4, 2), NumberStyles.None, CultureInfo.InvariantCulture);
        var cardDate = new DateOnly(year, month, day);
        var txnDate = DateOnly.FromDateTime(when.UtcDateTime);
        return cardDate.CompareTo(txnDate);
    }

    private static string TrimPanPadding(string panHex)
    {
        int f = panHex.IndexOf('F', StringComparison.OrdinalIgnoreCase);
        return f >= 0 ? panHex[..f] : panHex;
    }

    private static Result<T, TerminalError> Fail<T>(string step, string message) =>
        Result<T, TerminalError>.Fail(new TerminalError(step, message));
}

/// <summary>A trivial primitive-TLV byte builder, used where a full node is overkill.</summary>
internal readonly struct TlvNodeData(string tagHex, byte[] value)
{
    public byte[] ToBytes() => TlvNode.Primitive(Tag.Parse(tagHex), value).ToBytes();
}
