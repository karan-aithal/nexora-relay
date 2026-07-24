using System.Buffers;
using System.Globalization;
using System.Text;
using OpenForecourt.Emv.BerTlv;

namespace OpenForecourt.VirtualCard;

/// <summary>
/// The card side of an EMV conversation: an APDU responder driven by a
/// <see cref="CardProfile"/>. Give it a command APDU, it returns a response APDU
/// (data + status word). It holds the small amount of state a real card holds across a
/// transaction — which application is selected, whether GET PROCESSING OPTIONS has run,
/// and the Application Transaction Counter.
/// </summary>
/// <remarks>
/// This is not a mock. It builds real BER-TLV using the same codec the terminal parses
/// with, and answers with real ISO 7816-4 status words, so an in-process transaction
/// exercises the whole EMV message flow. Selecting a physical card instead is a change of
/// <see cref="Abstractions.Ports.ICardReader"/> implementation, not of this class.
/// </remarks>
public sealed class VirtualCard(CardProfile profile)
{
    /// <summary>The PPSE DF Name — the contactless "which apps do you have?" file.</summary>
    public const string PpseName = "2PAY.SYS.DDF01";

    /// <summary>The PSE DF Name — the contact application-directory file.</summary>
    public const string PseName = "1PAY.SYS.DDF01";

    private readonly CardProfile _profile = profile;

    private CardApplication? _selected;
    private bool _gpoDone;
    private ushort _atc;

    /// <summary>The profile this card presents.</summary>
    public CardProfile Profile => _profile;

    /// <summary>
    /// Processes one command APDU and returns the response APDU (response data followed by
    /// the two status-word bytes). Never throws: a malformed command yields <c>6700</c>.
    /// </summary>
    public byte[] Process(ReadOnlySpan<byte> commandApdu)
    {
        if (!CommandApdu.TryParse(commandApdu, out var apdu))
        {
            return StatusWord.WrongLength.Bytes;
        }

        return apdu.Ins switch
        {
            0xA4 => Select(apdu),
            0xA8 => GetProcessingOptions(apdu),
            0xB2 => ReadRecord(apdu),
            0xCA => GetData(apdu),
            0xAE => GenerateAc(apdu),
            _ => StatusWord.InstructionNotSupported.Bytes,
        };
    }

    private byte[] Select(CommandApdu apdu)
    {
        if (apdu.P1 != 0x04)
        {
            return StatusWord.IncorrectP1P2.Bytes; // only "select by name" is supported
        }

        string? name = TryAscii(apdu.Data.Span);

        if (name == PpseName)
        {
            return _profile.SupportsPpse ? Respond(BuildPpseFci()) : StatusWord.FileNotFound.Bytes;
        }

        if (name == PseName)
        {
            return _profile.SupportsPse ? Respond(BuildPseFci()) : StatusWord.FileNotFound.Bytes;
        }

        // Select by AID: match an application whose AID begins with the requested bytes.
        var match = _profile.Applications.FirstOrDefault(a => StartsWith(a.Aid, apdu.Data.Span));
        if (match is null)
        {
            return StatusWord.FileNotFound.Bytes;
        }

        _selected = match;
        _gpoDone = false;
        _atc = ushort.Parse(match.GenerateAc.Atc, NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        return Respond(BuildAidFci(match));
    }

    private byte[] GetProcessingOptions(CommandApdu apdu)
    {
        _ = apdu;
        if (_selected is null)
        {
            return StatusWord.ConditionsNotSatisfied.Bytes; // GPO before any AID selected
        }

        // ATC increments once per transaction, at GPO.
        _atc++;
        _gpoDone = true;

        var response = TlvNode.Constructed(Tag.Parse("77"),
            Primitive("82", _selected.Aip),
            Primitive("94", _selected.Afl));
        return Respond(response);
    }

    private byte[] ReadRecord(CommandApdu apdu)
    {
        // P2 = (SFI << 3) | 0b100 for "record number in P1 referencing this SFI".
        if ((apdu.P2 & 0x07) != 0x04)
        {
            return StatusWord.IncorrectP1P2.Bytes;
        }

        int sfi = apdu.P2 >> 3;
        int record = apdu.P1;

        // The PSE application directory lives at a reserved SFI and is read before selection.
        if (_profile.SupportsPse && sfi == _profile.PseDirectorySfi)
        {
            return record == 1
                ? Respond(BuildPseDirectoryRecord())
                : StatusWord.RecordNotFound.Bytes;
        }

        if (_selected is null)
        {
            return StatusWord.ConditionsNotSatisfied.Bytes;
        }

        string key = $"{sfi}:{record}";
        if (!_selected.Records.TryGetValue(key, out var fields))
        {
            return StatusWord.RecordNotFound.Bytes;
        }

        var template = TlvNode.Constructed(Tag.Parse("70"), fields.Select(kv => Primitive(kv.Key, kv.Value)).ToArray());
        return Respond(template);
    }

    private byte[] GetData(CommandApdu apdu)
    {
        if (_selected is null)
        {
            return StatusWord.ConditionsNotSatisfied.Bytes;
        }

        string tagHex = $"{apdu.P1:X2}{apdu.P2:X2}";

        // The ATC counter is live state, not a static profile value.
        if (tagHex == "9F36")
        {
            return Respond(Primitive("9F36", _atc.ToString("X4", CultureInfo.InvariantCulture)));
        }

        if (_selected.GetData.TryGetValue(tagHex, out var value))
        {
            return Respond(Primitive(tagHex, value));
        }

        return StatusWord.ReferencedDataNotFound.Bytes;
    }

    private byte[] GenerateAc(CommandApdu apdu)
    {
        _ = apdu;
        if (!_gpoDone || _selected is null)
        {
            return StatusWord.ConditionsNotSatisfied.Bytes; // GENERATE AC before GPO
        }

        var ac = _selected.GenerateAc;
        var response = TlvNode.Constructed(Tag.Parse("77"),
            Primitive("9F27", ac.Cid),
            Primitive("9F36", _atc.ToString("X4", CultureInfo.InvariantCulture)),
            Primitive("9F26", ac.Cryptogram),
            Primitive("9F10", ac.Iad));
        return Respond(response);
    }

    private TlvNode BuildPpseFci()
    {
        var entries = _profile.Applications
            .OrderBy(a => a.Priority)
            .Select(a => TlvNode.Constructed(Tag.Parse("61"),
                Primitive("4F", a.Aid),
                Primitive("87", PriorityByte(a.Priority)),
                PrimitiveAscii("50", a.Label)))
            .ToArray();

        return TlvNode.Constructed(Tag.Parse("6F"),
            PrimitiveAscii("84", PpseName),
            TlvNode.Constructed(Tag.Parse("A5"),
                TlvNode.Constructed(Tag.Parse("BF0C"), entries)));
    }

    private TlvNode BuildPseFci() =>
        TlvNode.Constructed(Tag.Parse("6F"),
            PrimitiveAscii("84", PseName),
            TlvNode.Constructed(Tag.Parse("A5"),
                Primitive("88", ((byte)_profile.PseDirectorySfi).ToString("X2", CultureInfo.InvariantCulture))));

    private TlvNode BuildPseDirectoryRecord()
    {
        var entries = _profile.Applications
            .OrderBy(a => a.Priority)
            .Select(a => TlvNode.Constructed(Tag.Parse("61"),
                Primitive("4F", a.Aid),
                Primitive("87", PriorityByte(a.Priority)),
                PrimitiveAscii("50", a.Label)))
            .ToArray();

        return TlvNode.Constructed(Tag.Parse("70"), entries);
    }

    private static TlvNode BuildAidFci(CardApplication app)
    {
        var proprietary = new List<TlvNode>
        {
            PrimitiveAscii("50", app.Label),
            Primitive("87", PriorityByte(app.Priority)),
        };

        if (app.Pdol.Count > 0)
        {
            proprietary.Add(Primitive("9F38", EncodeDol(app.Pdol)));
        }

        return TlvNode.Constructed(Tag.Parse("6F"),
            Primitive("84", app.Aid),
            TlvNode.Constructed(Tag.Parse("A5"), [.. proprietary]));
    }

    /// <summary>Encodes a DOL: each entry is its tag bytes followed by a one-byte length.</summary>
    private static string EncodeDol(IReadOnlyList<DolEntry> dol)
    {
        var writer = new ArrayBufferWriter<byte>();
        foreach (var e in dol)
        {
            writer.Write(Tag.Parse(e.Tag).Bytes);
            writer.Write([(byte)e.Length]);
        }

        return Convert.ToHexString(writer.WrittenSpan);
    }

    private static byte[] Respond(TlvNode node)
    {
        var writer = new ArrayBufferWriter<byte>();
        node.WriteTo(writer);
        return [.. writer.WrittenSpan, .. StatusWord.Success.Bytes];
    }

    private static TlvNode Primitive(string tagHex, string valueHex) =>
        TlvNode.Primitive(Tag.Parse(tagHex), Convert.FromHexString(valueHex));

    private static TlvNode PrimitiveAscii(string tagHex, string ascii) =>
        TlvNode.Primitive(Tag.Parse(tagHex), Encoding.ASCII.GetBytes(ascii));

    private static string PriorityByte(int priority) =>
        ((byte)(priority & 0x0F)).ToString("X2", CultureInfo.InvariantCulture);

    private static bool StartsWith(string aidHex, ReadOnlySpan<byte> prefix)
    {
        if (prefix.IsEmpty)
        {
            return false;
        }

        var aid = Convert.FromHexString(aidHex);
        return prefix.Length <= aid.Length && aid.AsSpan(0, prefix.Length).SequenceEqual(prefix);
    }

    private static string? TryAscii(ReadOnlySpan<byte> data)
    {
        foreach (byte b in data)
        {
            if (b is < 0x20 or > 0x7E)
            {
                return null;
            }
        }

        return Encoding.ASCII.GetString(data);
    }
}
