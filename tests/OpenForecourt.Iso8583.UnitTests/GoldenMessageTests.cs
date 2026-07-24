using System.Globalization;
using OpenForecourt.Iso8583;
using Xunit;

namespace OpenForecourt.Iso8583.UnitTests;

/// <summary>
/// Byte-exact tests against hand-annotated messages in <c>GoldenMessages/</c>.
/// </summary>
/// <remarks>
/// <para>
/// A round-trip test proves the codec is self-consistent; it cannot prove the bytes on the
/// wire are right — an encoder and decoder can agree on the same wrong format forever. The
/// golden files pin the actual bytes, and each one carries a hex-annotated commentary so
/// the expected value can be checked by eye against <c>docs/protocol-iso8583.md</c>.
/// </para>
/// <para>
/// The expected hex was produced from the protocol document by a separate throwaway
/// implementation (see the Phase 1 walkthrough), not by the code under test, so a bug in the
/// C# encoder cannot silently define its own truth.
/// </para>
/// </remarks>
public class GoldenMessageTests
{
    private static readonly string GoldenDirectory =
        Path.Combine(AppContext.BaseDirectory, "GoldenMessages");

    public static TheoryData<string> GoldenFiles()
    {
        var data = new TheoryData<string>();
        foreach (string path in Directory.EnumerateFiles(GoldenDirectory, "*.txt").Order(StringComparer.Ordinal))
        {
            data.Add(Path.GetFileName(path));
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(GoldenFiles))]
    public void Encodes_to_the_expected_bytes(string fileName)
    {
        var golden = GoldenMessage.Load(Path.Combine(GoldenDirectory, fileName));

        string actual = Convert.ToHexString(new Iso8583Codec().Encode(golden.Message));

        Assert.Equal(golden.Hex, actual);
    }

    [Theory]
    [MemberData(nameof(GoldenFiles))]
    public void Decodes_the_expected_bytes_back_to_the_same_fields(string fileName)
    {
        var golden = GoldenMessage.Load(Path.Combine(GoldenDirectory, fileName));

        var decoded = new Iso8583Codec().TryDecode(Convert.FromHexString(golden.Hex));

        Assert.True(decoded.IsSuccess, decoded.IsError ? decoded.Error.ToString() : null);
        Assert.Equal(golden.Message.Mti, decoded.Value.Mti);
        Assert.Equal(golden.Message.PresentFields, decoded.Value.PresentFields);
        foreach (int field in golden.Message.PresentFields)
        {
            Assert.Equal(golden.Message[field], decoded.Value[field]);
        }
    }

    /// <summary>A golden file: annotation comments, the fields, and the expected hex.</summary>
    private sealed record GoldenMessage(Iso8583Message Message, string Hex)
    {
        public static GoldenMessage Load(string path)
        {
            string? mti = null;
            string? hex = null;
            var fields = new List<(int Number, string Value)>();

            foreach (string raw in File.ReadLines(path))
            {
                string line = raw.Trim();
                if (line.Length == 0 || line.StartsWith('#'))
                {
                    continue;
                }

                int colon = line.IndexOf(':', StringComparison.Ordinal);
                string key = line[..colon].Trim();
                string value = line[(colon + 1)..].Trim();

                if (key == "mti")
                {
                    mti = value;
                }
                else if (key == "hex")
                {
                    hex = value;
                }
                else if (key.StartsWith("field ", StringComparison.Ordinal))
                {
                    fields.Add((int.Parse(key[6..], CultureInfo.InvariantCulture), value));
                }
                else
                {
                    throw new InvalidDataException($"{Path.GetFileName(path)}: unrecognised key '{key}'.");
                }
            }

            Assert.NotNull(mti);
            Assert.NotNull(hex);

            var builder = Iso8583Message.Create(mti);
            foreach ((int number, string value) in fields)
            {
                builder.Set(number, value);
            }

            return new GoldenMessage(builder.Build(), hex);
        }
    }
}
