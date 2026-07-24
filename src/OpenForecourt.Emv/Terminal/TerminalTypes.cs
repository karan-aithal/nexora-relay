using OpenForecourt.Abstractions.Domain;
using OpenForecourt.Emv.BerTlv;

namespace OpenForecourt.Emv.Terminal;

/// <summary>One command/response APDU exchange, kept for the trace.</summary>
/// <param name="Command">The command APDU sent to the card.</param>
/// <param name="Response">The response APDU (data + status word) received.</param>
public readonly record struct ApduExchange(byte[] Command, byte[] Response)
{
    /// <summary>The trailing two-byte status word of the response.</summary>
    public ushort StatusWord => Response.Length >= 2
        ? (ushort)((Response[^2] << 8) | Response[^1])
        : (ushort)0;
}

/// <summary>The cryptogram the terminal decided to ask the card for in GENERATE AC.</summary>
public enum CryptogramType
{
    /// <summary>Application Authentication Cryptogram — decline.</summary>
    Aac,

    /// <summary>Transaction Certificate — approve offline.</summary>
    Tc,

    /// <summary>Authorisation Request Cryptogram — go online.</summary>
    Arqc,
}

/// <summary>The cardholder verification method the terminal selected from the CVM list.</summary>
public enum CvmMethod
{
    /// <summary>No cardholder verification was required.</summary>
    NoCvm,

    /// <summary>Online PIN — the PIN block is verified by the issuer.</summary>
    OnlinePin,

    /// <summary>Signature.</summary>
    Signature,

    /// <summary>Cardholder verification could not be satisfied.</summary>
    Failed,
}

/// <summary>What the terminal concluded, from the card's final GENERATE AC answer.</summary>
public enum TerminalDecision
{
    /// <summary>The card returned an ARQC: the transaction must be sent to the issuer online.</summary>
    OnlineAuthorisationRequested,

    /// <summary>The card returned an AAC: the transaction is declined.</summary>
    DeclinedOffline,

    /// <summary>The card returned a TC: the transaction is approved offline.</summary>
    ApprovedOffline,
}

/// <summary>Why the terminal flow could not complete.</summary>
/// <param name="Step">The state at which it stopped.</param>
/// <param name="Message">A human-readable diagnostic.</param>
public readonly record struct TerminalError(string Step, string Message)
{
    /// <inheritdoc />
    public override string ToString() => $"{Step}: {Message}";
}

/// <summary>The result of a completed EMV terminal transaction flow.</summary>
public sealed class TerminalOutcome
{
    /// <summary>The AID of the selected application, hex.</summary>
    public required string SelectedAid { get; init; }

    /// <summary>The masked PAN. Cleartext never leaves this object (CLAUDE.md section 7).</summary>
    public required Pan Pan { get; init; }

    /// <summary>The cryptogram the terminal requested.</summary>
    public required CryptogramType Requested { get; init; }

    /// <summary>The cardholder verification method selected.</summary>
    public required CvmMethod Cvm { get; init; }

    /// <summary>The final decision from the card's cryptogram type.</summary>
    public required TerminalDecision Decision { get; init; }

    /// <summary>Terminal Verification Results (tag 95), 5 bytes.</summary>
    public required byte[] Tvr { get; init; }

    /// <summary>The assembled ISO 8583 field 55 (EMV data) — a concatenation of TLV objects.</summary>
    public required byte[] Field55 { get; init; }
}

/// <summary>
/// A flat store of primitive EMV data elements collected during a transaction, keyed by tag.
/// Both card-read tags and terminal-resident tags land here; DOLs and field 55 are built
/// from it.
/// </summary>
internal sealed class TagStore
{
    private readonly Dictionary<uint, byte[]> _values = [];

    public void Set(Tag tag, byte[] value) => _values[tag.Value] = value;

    public void Set(string tagHex, byte[] value) => _values[Tag.Parse(tagHex).Value] = value;

    public bool TryGet(Tag tag, out byte[] value) => _values.TryGetValue(tag.Value, out value!);

    public byte[]? Get(string tagHex) => _values.TryGetValue(Tag.Parse(tagHex).Value, out var v) ? v : null;

    /// <summary>Flattens a parsed TLV tree, storing every primitive leaf under its tag.</summary>
    public void Absorb(IReadOnlyList<TlvNode> nodes)
    {
        foreach (var node in nodes)
        {
            if (node.Tag.IsConstructed)
            {
                Absorb(node.Children);
            }
            else
            {
                _values[node.Tag.Value] = node.Value.ToArray();
            }
        }
    }
}
