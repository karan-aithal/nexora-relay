using System.Security.Cryptography;
using OpenForecourt.Abstractions;
using OpenForecourt.Abstractions.Domain;
using OpenForecourt.Crypto.Dukpt;

namespace OpenForecourt.Crypto.Pin;

/// <summary>
/// ISO 9564-1 PIN blocks. Format 0 (the classic PAN-XOR block, TDES) is the primary path
/// and encrypts under a DUKPT PIN key; Format 4 (AES) is the modern block.
/// </summary>
/// <remarks>
/// <para>
/// Nothing in this type logs, and no method returns or accepts a PIN in any form other than
/// the caller's own string and the encrypted block — there is no diagnostic surface a PIN
/// could escape through (CLAUDE.md §7.5). The PAN-leak test additionally scans every sink.
/// </para>
/// <para>
/// <b>Verification status.</b> The Format 0 <i>clear</i> block construction is checked against
/// a known vector (PIN 1234 / a test PAN). Encryption round-trips (encode → encrypt → decrypt
/// → same PIN) prove the cipher pairing. Format 4's block layout follows ISO 9564-1:2017 but
/// is marked <c>SPEC-UNVERIFIED</c>: it is proven by round-trip only, not against a published
/// Format 4 vector, and it uses a raw AES key because AES-DUKPT (X9.24-3) is the optional
/// stretch and is not implemented.
/// </para>
/// </remarks>
public static class PinBlock
{
    /// <summary>Builds the 8-byte cleartext Format 0 block: PIN field XOR PAN field.</summary>
    public static byte[] EncodeFormat0(string pin, Pan pan)
    {
        ValidatePin(pin);
        byte[] pinField = Format0PinField(pin);
        byte[] panField = Format0PanField(pan);
        for (int i = 0; i < 8; i++)
        {
            pinField[i] ^= panField[i];
        }

        return pinField;
    }

    /// <summary>Encrypts a Format 0 PIN block under a TDES PIN key (8-byte ciphertext).</summary>
    public static byte[] EncryptFormat0(string pin, Pan pan, ReadOnlySpan<byte> tdesKey) =>
        DukptTdes.EncryptBlock(tdesKey, EncodeFormat0(pin, pan));

    /// <summary>Decrypts a Format 0 block and recovers the PIN, or reports a malformed block.</summary>
    public static Result<string, PinBlockError> DecryptFormat0(
        ReadOnlySpan<byte> cipher, Pan pan, ReadOnlySpan<byte> tdesKey)
    {
        if (cipher.Length != 8)
        {
            return Result<string, PinBlockError>.Fail(PinBlockError.WrongBlockLength);
        }

        byte[] clear = DukptTdes.DecryptBlock(tdesKey, cipher);
        byte[] panField = Format0PanField(pan);
        for (int i = 0; i < 8; i++)
        {
            clear[i] ^= panField[i];
        }

        // clear now holds the PIN field: nibble0 = format (0), nibble1 = length, then digits.
        if ((clear[0] & 0xF0) != 0x00)
        {
            return Result<string, PinBlockError>.Fail(PinBlockError.BadFormatNibble);
        }

        int length = clear[0] & 0x0F;
        return ExtractPin(clear, startNibble: 2, length);
    }

    /// <summary>Encrypts a Format 4 PIN block under an AES key (16-byte ciphertext).</summary>
    public static byte[] EncryptFormat4(string pin, Pan pan, ReadOnlySpan<byte> aesKey)
    {
        ValidatePin(pin);
        byte[] pinField = Format4PinField(pin);
        byte[] panField = Format4PanField(pan);

        using var aes = Aes.Create();
        aes.Mode = CipherMode.ECB;
        aes.Padding = PaddingMode.None;
        aes.Key = aesKey.ToArray();

        byte[] blockA = aes.EncryptEcb(pinField, PaddingMode.None);
        for (int i = 0; i < 16; i++)
        {
            blockA[i] ^= panField[i];
        }

        return aes.EncryptEcb(blockA, PaddingMode.None);
    }

    /// <summary>Decrypts a Format 4 block and recovers the PIN, or reports a malformed block.</summary>
    public static Result<string, PinBlockError> DecryptFormat4(
        ReadOnlySpan<byte> cipher, Pan pan, ReadOnlySpan<byte> aesKey)
    {
        if (cipher.Length != 16)
        {
            return Result<string, PinBlockError>.Fail(PinBlockError.WrongBlockLength);
        }

        byte[] panField = Format4PanField(pan);
        using var aes = Aes.Create();
        aes.Mode = CipherMode.ECB;
        aes.Padding = PaddingMode.None;
        aes.Key = aesKey.ToArray();

        byte[] blockA = aes.DecryptEcb(cipher, PaddingMode.None);
        for (int i = 0; i < 16; i++)
        {
            blockA[i] ^= panField[i];
        }

        byte[] pinField = aes.DecryptEcb(blockA, PaddingMode.None);
        if ((pinField[0] & 0xF0) != 0x40)
        {
            return Result<string, PinBlockError>.Fail(PinBlockError.BadFormatNibble);
        }

        int length = pinField[0] & 0x0F;
        return ExtractPin(pinField, startNibble: 2, length);
    }

    private static byte[] Format0PinField(string pin)
    {
        // 0 | L | PIN digits | F-fill to 16 nibbles.
        Span<byte> nibbles = stackalloc byte[16];
        nibbles[0] = 0x0;
        nibbles[1] = (byte)pin.Length;
        for (int i = 0; i < pin.Length; i++)
        {
            nibbles[2 + i] = (byte)(pin[i] - '0');
        }

        for (int i = 2 + pin.Length; i < 16; i++)
        {
            nibbles[i] = 0xF;
        }

        return PackNibbles(nibbles);
    }

    private static byte[] Format0PanField(Pan pan)
    {
        // 0000 | rightmost 12 PAN digits excluding the check digit, right-justified.
        string digits = pan.Reveal();
        string withoutCheck = digits[..^1];
        string twelve = withoutCheck.Length >= 12
            ? withoutCheck[^12..]
            : withoutCheck.PadLeft(12, '0');

        Span<byte> nibbles = stackalloc byte[16];
        nibbles[0] = 0;
        nibbles[1] = 0;
        nibbles[2] = 0;
        nibbles[3] = 0;
        for (int i = 0; i < 12; i++)
        {
            nibbles[4 + i] = (byte)(twelve[i] - '0');
        }

        return PackNibbles(nibbles);
    }

    // SPEC-UNVERIFIED: Format 4 plaintext PIN field = 4 | L | PIN | 'A'-fill to 16 nibbles,
    // then 8 bytes of random fill (ISO 9564-1:2017). Proven by round-trip only.
    private static byte[] Format4PinField(string pin)
    {
        Span<byte> nibbles = stackalloc byte[16];
        nibbles[0] = 0x4;
        nibbles[1] = (byte)pin.Length;
        for (int i = 0; i < pin.Length; i++)
        {
            nibbles[2 + i] = (byte)(pin[i] - '0');
        }

        for (int i = 2 + pin.Length; i < 16; i++)
        {
            nibbles[i] = 0xA;
        }

        byte[] field = new byte[16];
        PackNibbles(nibbles).CopyTo(field, 0);
        RandomNumberGenerator.Fill(field.AsSpan(8, 8));
        return field;
    }

    // SPEC-UNVERIFIED: Format 4 PAN field = (PAN length - 12) nibble | PAN digits | 0-fill to
    // 32 nibbles (16 bytes). Proven by round-trip only.
    private static byte[] Format4PanField(Pan pan)
    {
        string digits = pan.Reveal();
        Span<byte> nibbles = stackalloc byte[32];
        nibbles[0] = (byte)Math.Clamp(digits.Length - 12, 0, 15);
        for (int i = 0; i < digits.Length && i < 31; i++)
        {
            nibbles[1 + i] = (byte)(digits[i] - '0');
        }

        return PackNibbles(nibbles);
    }

    private static Result<string, PinBlockError> ExtractPin(ReadOnlySpan<byte> field, int startNibble, int length)
    {
        if (length is < 4 or > 12)
        {
            return Result<string, PinBlockError>.Fail(PinBlockError.BadLength);
        }

        Span<char> pin = stackalloc char[length];
        for (int i = 0; i < length; i++)
        {
            int nibble = ReadNibble(field, startNibble + i);
            if (nibble > 9)
            {
                return Result<string, PinBlockError>.Fail(PinBlockError.NonDigitPin);
            }

            pin[i] = (char)('0' + nibble);
        }

        return Result<string, PinBlockError>.Ok(new string(pin));
    }

    private static byte[] PackNibbles(ReadOnlySpan<byte> nibbles)
    {
        var bytes = new byte[nibbles.Length / 2];
        for (int i = 0; i < bytes.Length; i++)
        {
            bytes[i] = (byte)((nibbles[2 * i] << 4) | nibbles[2 * i + 1]);
        }

        return bytes;
    }

    private static int ReadNibble(ReadOnlySpan<byte> bytes, int index) =>
        (index & 1) == 0 ? bytes[index / 2] >> 4 : bytes[index / 2] & 0x0F;

    private static void ValidatePin(string pin)
    {
        ArgumentNullException.ThrowIfNull(pin);
        if (pin.Length is < 4 or > 12)
        {
            throw new ArgumentException("A PIN is 4 to 12 digits.", nameof(pin));
        }

        foreach (char c in pin)
        {
            if (c is < '0' or > '9')
            {
                throw new ArgumentException("A PIN is digits only.", nameof(pin));
            }
        }
    }
}

/// <summary>Ways a PIN-block decode can fail (all are malformed-input outcomes, not exceptions).</summary>
public enum PinBlockError
{
    /// <summary>The ciphertext was not the expected block length.</summary>
    WrongBlockLength,

    /// <summary>The recovered block's format nibble was wrong (wrong key or wrong format).</summary>
    BadFormatNibble,

    /// <summary>The encoded PIN length was outside 4..12.</summary>
    BadLength,

    /// <summary>A PIN position held a non-digit nibble.</summary>
    NonDigitPin,
}
