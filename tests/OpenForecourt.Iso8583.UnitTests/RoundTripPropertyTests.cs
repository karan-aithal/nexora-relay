using Xunit;
using Xunit.Abstractions;

namespace OpenForecourt.Iso8583.UnitTests;

/// <summary>
/// Property-based round trip: for any valid message, <c>decode(encode(m)) == m</c>.
/// </summary>
/// <remarks>
/// <para>
/// The generator is a seeded <see cref="Random"/> rather than a property-testing package.
/// A dependency would buy shrinking and a nicer DSL; a fixed seed buys reproducibility,
/// which is what actually matters when a nightly CI run fails — the failing case is
/// regenerated exactly by re-running with the same seed, printed in the assertion message.
/// The generator is ~30 lines and understanding it end to end is the point of the exercise.
/// </para>
/// <para>
/// It generates every field the dialect defines, at random lengths including the empty and
/// maximum cases, so LLVAR/LLLVAR boundaries and the secondary bitmap are all exercised.
/// </para>
/// </remarks>
public class RoundTripPropertyTests(ITestOutputHelper output)
{
    private const int Cases = 2000;

    [Theory]
    [InlineData(20260724)]
    [InlineData(1)]
    public void Any_valid_message_survives_encode_then_decode(int seed)
    {
        var codec = new Iso8583Codec();
        var random = new Random(seed);
        int withSecondaryBitmap = 0;

        for (int i = 0; i < Cases; i++)
        {
            var original = GenerateMessage(random);
            byte[] encoded = codec.Encode(original);
            var decoded = codec.TryDecode(encoded);

            Assert.True(decoded.IsSuccess,
                $"seed {seed}, case {i}: {(decoded.IsError ? decoded.Error.ToString() : string.Empty)}\n{original}");
            Assert.Equal(original.Mti, decoded.Value.Mti);
            Assert.Equal(original.PresentFields, decoded.Value.PresentFields);

            foreach (int field in original.PresentFields)
            {
                Assert.Equal(original[field], decoded.Value[field]);
            }

            // Re-encoding the decoded message must produce the identical bytes, which catches
            // any padding or canonicalisation asymmetry the field comparison would miss.
            Assert.Equal(encoded, codec.Encode(decoded.Value));

            if (original.Bitmap.HasSecondary)
            {
                withSecondaryBitmap++;
            }
        }

        output.WriteLine($"{Cases} messages, {withSecondaryBitmap} with a secondary bitmap.");
        Assert.True(withSecondaryBitmap > 0, "the generator never produced a field above 64.");
    }

    private static Iso8583Message GenerateMessage(Random random)
    {
        string[] mtis = ["0100", "0110", "0200", "0210", "0400", "0410", "0800", "0810"];
        var builder = Iso8583Message.Create(mtis[random.Next(mtis.Length)]);

        foreach (int field in Iso8583Dialect.Ofc87.DefinedFields)
        {
            if (random.Next(2) == 0)
            {
                continue;
            }

            builder.Set(field, GenerateValue(Iso8583Dialect.Ofc87.Get(field), random));
        }

        return builder.Build();
    }

    private static string GenerateValue(FieldDefinition definition, Random random)
    {
        // Fixed fields are always at their full length; variable ones sweep 0..max, so the
        // empty and maximum-length cases both come up.
        int units = definition.Type == FieldType.Fixed
            ? definition.Length
            : random.Next(0, definition.Length + 1);

        return definition.Encoding switch
        {
            FieldEncoding.Numeric => RandomChars(random, units, '0', '9'),
            FieldEncoding.Alphanumeric => RandomChars(random, units, ' ', '~'),
            _ => RandomHex(random, units),
        };
    }

    private static string RandomChars(Random random, int count, char low, char high) =>
        string.Create(count, random, (span, rng) =>
        {
            for (int i = 0; i < span.Length; i++)
            {
                span[i] = (char)rng.Next(low, high + 1);
            }
        });

    private static string RandomHex(Random random, int byteCount)
    {
        byte[] bytes = new byte[byteCount];
        random.NextBytes(bytes);
        return Convert.ToHexString(bytes);
    }

    [Fact]
    public void Alphanumeric_values_that_end_in_spaces_are_a_known_ambiguity()
    {
        // Documented, not fixed: a fixed-length ans field is space-padded on the wire, so a
        // value whose own last character is a space is indistinguishable from padding. The
        // codec stores the padded form on both sides, which keeps the round trip exact; it
        // does not attempt to recover the sender's "intended" trailing spaces.
        var message = Iso8583Message.Create("0200").Set(Fields.TerminalId, "OPT ").Build();

        Assert.Equal("OPT     ", message[Fields.TerminalId]);
        Assert.Equal(8, message[Fields.TerminalId]!.Length);
    }

    [Fact]
    public void The_generator_produces_the_documented_field_set()
    {
        Assert.Equal(
            [2, 3, 4, 7, 11, 12, 13, 22, 37, 38, 39, 41, 42, 49, 55, 70, 90],
            Iso8583Dialect.Ofc87.DefinedFields.ToArray());

        Assert.Equal("OFC-87", Iso8583Dialect.Ofc87.Name);
        Assert.Equal("LLVAR n..19", Iso8583Dialect.Ofc87.Get(2).Descriptor);
        Assert.Equal("n 6", Iso8583Dialect.Ofc87.Get(11).Descriptor);
        Assert.Equal("LLLVAR b..999", Iso8583Dialect.Ofc87.Get(55).Descriptor);
    }
}
