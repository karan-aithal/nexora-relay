using System.Buffers.Binary;
using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography;

namespace OpenForecourt.Crypto.Dukpt;

/// <summary>
/// The DUKPT (Derived Unique Key Per Transaction) key-management scheme, TDES variant,
/// per ANSI X9.24-1. Both sides of the wire live here: the terminal derives a fresh key
/// from its Initial PIN Encryption Key and the current KSN; the host re-derives the same
/// key from the Base Derivation Key and the KSN it received. No key is ever transmitted.
/// </summary>
/// <remarks>
/// <para>
/// <b>Verification status.</b> The BDK→IPEK derivation is checked in tests against the
/// canonical X9.24 worked example (BDK <c>0123456789ABCDEFFEDCBA9876543210</c>,
/// KSN <c>FFFF9876543210E00000</c> → IPEK <c>6AC292FAA1315B4D858AB3A3D7D5933A</c>), the
/// single most widely reproduced DUKPT test vector. Per-transaction key advancement and the
/// PIN/data key variants are proven by round-trip self-consistency (terminal derives and
/// encrypts; host independently re-derives from the BDK and decrypts to the same plaintext)
/// rather than against external per-counter vectors, which were not sourced with certainty —
/// see <c>SPEC-UNVERIFIED</c> notes and the Phase 3 walkthrough. CLAUDE.md §11: an honest
/// gap beats a confidently wrong table.
/// </para>
/// <para>
/// TDES / single-DES are used deliberately: they are the algorithm the X9.24-1 scheme is
/// defined over. The weak-crypto analyzer warnings are suppressed at the primitives with
/// that justification; nothing here protects real cardholder data.
/// </para>
/// </remarks>
public static class DukptTdes
{
    // XOR mask applied to a 16-byte key to form its "variant" during derivation (X9.24-1).
    private static readonly byte[] KeyVariantMask =
        Convert.FromHexString("C0C0C0C000000000C0C0C0C000000000");

    // Working-key variant masks applied to the derived transaction key before use.
    private static readonly byte[] PinVariantMask =
        Convert.FromHexString("00000000000000FF00000000000000FF");

    // SPEC-UNVERIFIED: data-encryption variant mask and the extra self-encryption step below
    // follow the commonly-cited X9.24-1 request-data-key recipe but were not confirmed
    // against a published vector; the round-trip test proves internal consistency only.
    private static readonly byte[] DataVariantMask =
        Convert.FromHexString("0000000000FF00000000000000FF0000");

    /// <summary>
    /// Derives the Initial PIN Encryption Key for a device from the Base Derivation Key and
    /// the device's initial KSN. Injected into the terminal at manufacture; the BDK never is.
    /// </summary>
    public static byte[] DeriveIpek(ReadOnlySpan<byte> bdk, Ksn ksn)
    {
        if (bdk.Length != 16)
        {
            throw new ArgumentException("A TDES BDK is 16 bytes (two-key).", nameof(bdk));
        }

        // The IPEK is seeded from the leftmost 8 bytes of the KSN with the 21-bit counter
        // cleared (the low 5 bits of the 8th byte are part of the counter).
        Span<byte> ksnLeft8 = stackalloc byte[8];
        ksn.ToBytes().AsSpan(0, 8).CopyTo(ksnLeft8);
        ksnLeft8[7] &= 0xE0;

        byte[] bdkVariant = Xor(bdk, KeyVariantMask);
        byte[] left = TdesEcbEncryptBlock(bdk, ksnLeft8);
        byte[] right = TdesEcbEncryptBlock(bdkVariant, ksnLeft8);
        CryptographicOperations.ZeroMemory(bdkVariant);
        return Concat(left, right);
    }

    /// <summary>
    /// Derives the per-transaction key for a KSN from the IPEK, then applies the working-key
    /// variant for the requested purpose. This is what encrypts a PIN block or data element.
    /// </summary>
    public static byte[] DeriveTransactionKey(ReadOnlySpan<byte> ipek, Ksn ksn, DukptKeyType keyType)
    {
        byte[] baseKey = DeriveBaseKey(ipek, ksn);
        try
        {
            return keyType switch
            {
                DukptKeyType.PinEncryption => Xor(baseKey, PinVariantMask),
                DukptKeyType.DataEncryption => DataKey(baseKey),
                _ => throw new ArgumentOutOfRangeException(nameof(keyType)),
            };
        }
        finally
        {
            CryptographicOperations.ZeroMemory(baseKey);
        }
    }

    // The non-reversible derivation: start from the IPEK and, for each set counter bit from
    // high to low, fold that bit into the register and generate a new key. Earlier keys
    // cannot be recovered from a later one — that is the "forward security" of DUKPT.
    private static byte[] DeriveBaseKey(ReadOnlySpan<byte> ipek, Ksn ksn)
    {
        ulong reg = ksn.CounterlessLow64;
        uint counter = ksn.Counter;

        byte[] curKey = ipek.ToArray();
        for (uint shift = 0x100000; shift != 0; shift >>= 1)
        {
            if ((counter & shift) == 0)
            {
                continue;
            }

            reg |= shift;
            byte[] next = GenerateKey(curKey, reg);
            CryptographicOperations.ZeroMemory(curKey);
            curKey = next;
        }

        return curKey;
    }

    // One "non-reversible key generation" round: produce a 16-byte key from a 16-byte key
    // and the 8-byte KSN register, using the register-encrypt core under the key and its
    // variant for the two halves.
    private static byte[] GenerateKey(byte[] key, ulong reg)
    {
        Span<byte> regBytes = stackalloc byte[8];
        BinaryPrimitives.WriteUInt64BigEndian(regBytes, reg);

        byte[] keyVariant = Xor(key, KeyVariantMask);
        byte[] left = EncryptRegister(key, regBytes);
        byte[] right = EncryptRegister(keyVariant, regBytes);
        CryptographicOperations.ZeroMemory(keyVariant);
        return Concat(left, right);
    }

    // bottom = DES_encrypt(keyLeft, reg XOR keyRight) XOR keyRight. Single DES under the left
    // half; the right half is mixed in on both sides. This is the irreversible step.
    private static byte[] EncryptRegister(byte[] key, ReadOnlySpan<byte> reg)
    {
        ReadOnlySpan<byte> keyLeft = key.AsSpan(0, 8);
        ReadOnlySpan<byte> keyRight = key.AsSpan(8, 8);

        Span<byte> bottom = stackalloc byte[8];
        for (int i = 0; i < 8; i++)
        {
            bottom[i] = (byte)(reg[i] ^ keyRight[i]);
        }

        byte[] enc = DesEcbEncryptBlock(keyLeft, bottom);
        for (int i = 0; i < 8; i++)
        {
            enc[i] ^= keyRight[i];
        }

        return enc;
    }

    // Data-encryption working key: variant the transaction key, then TDES-encrypt each half
    // of that variant under itself. SPEC-UNVERIFIED (see field note above).
    private static byte[] DataKey(byte[] baseKey)
    {
        byte[] variant = Xor(baseKey, DataVariantMask);
        try
        {
            byte[] left = TdesEcbEncryptBlock(variant, variant.AsSpan(0, 8));
            byte[] right = TdesEcbEncryptBlock(variant, variant.AsSpan(8, 8));
            return Concat(left, right);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(variant);
        }
    }

    /// <summary>Encrypts one 8-byte block with a working key in TDES-ECB (no padding).</summary>
    public static byte[] EncryptBlock(ReadOnlySpan<byte> key, ReadOnlySpan<byte> block) =>
        TdesEcbEncryptBlock(key, block);

    /// <summary>Decrypts one 8-byte block with a working key in TDES-ECB (no padding).</summary>
    public static byte[] DecryptBlock(ReadOnlySpan<byte> key, ReadOnlySpan<byte> block) =>
        TdesEcbDecryptBlock(key, block);

    [SuppressMessage("Security", "CA5350:Do Not Use Weak Cryptographic Algorithms",
        Justification = "TDES is the algorithm ANSI X9.24-1 DUKPT is defined over; test keys only.")]
    [SuppressMessage("Security", "CA5351:Do Not Use Broken Cryptographic Algorithms",
        Justification = "TDES is mandated by the X9.24-1 scheme; no real cardholder data is protected.")]
    private static byte[] TdesEcbEncryptBlock(ReadOnlySpan<byte> key, ReadOnlySpan<byte> block)
    {
        using var tdes = TripleDES.Create();
        tdes.Mode = CipherMode.ECB;
        tdes.Padding = PaddingMode.None;
        tdes.Key = ExpandTdesKey(key);
        return tdes.EncryptEcb(block, PaddingMode.None);
    }

    [SuppressMessage("Security", "CA5350:Do Not Use Weak Cryptographic Algorithms",
        Justification = "TDES is the algorithm ANSI X9.24-1 DUKPT is defined over; test keys only.")]
    [SuppressMessage("Security", "CA5351:Do Not Use Broken Cryptographic Algorithms",
        Justification = "TDES is mandated by the X9.24-1 scheme; no real cardholder data is protected.")]
    private static byte[] TdesEcbDecryptBlock(ReadOnlySpan<byte> key, ReadOnlySpan<byte> block)
    {
        using var tdes = TripleDES.Create();
        tdes.Mode = CipherMode.ECB;
        tdes.Padding = PaddingMode.None;
        tdes.Key = ExpandTdesKey(key);
        return tdes.DecryptEcb(block, PaddingMode.None);
    }

    [SuppressMessage("Security", "CA5350:Do Not Use Weak Cryptographic Algorithms",
        Justification = "Single DES is the register-encrypt primitive of X9.24-1 DUKPT; test keys only.")]
    [SuppressMessage("Security", "CA5351:Do Not Use Broken Cryptographic Algorithms",
        Justification = "Single DES is mandated by the X9.24-1 derivation; no real cardholder data is protected.")]
    private static byte[] DesEcbEncryptBlock(ReadOnlySpan<byte> key, ReadOnlySpan<byte> block)
    {
        using var des = DES.Create();
        des.Mode = CipherMode.ECB;
        des.Padding = PaddingMode.None;
        des.Key = key.ToArray();
        return des.EncryptEcb(block, PaddingMode.None);
    }

    // .NET's TripleDES wants a 24-byte key; expand a 16-byte two-key TDES to K1|K2|K1.
    private static byte[] ExpandTdesKey(ReadOnlySpan<byte> key)
    {
        if (key.Length == 24)
        {
            return key.ToArray();
        }

        if (key.Length != 16)
        {
            throw new ArgumentException("A TDES working key is 16 or 24 bytes.", nameof(key));
        }

        var expanded = new byte[24];
        key.CopyTo(expanded);
        key[..8].CopyTo(expanded.AsSpan(16));
        return expanded;
    }

    private static byte[] Xor(ReadOnlySpan<byte> a, ReadOnlySpan<byte> b)
    {
        var result = new byte[a.Length];
        for (int i = 0; i < a.Length; i++)
        {
            result[i] = (byte)(a[i] ^ b[i]);
        }

        return result;
    }

    private static byte[] Concat(byte[] left, byte[] right)
    {
        var result = new byte[left.Length + right.Length];
        left.CopyTo(result, 0);
        right.CopyTo(result, left.Length);
        return result;
    }
}

/// <summary>The working-key purpose a derived DUKPT key is variant-ed for.</summary>
public enum DukptKeyType
{
    /// <summary>PIN-block encryption key (the primary DUKPT use).</summary>
    PinEncryption,

    /// <summary>Data-encryption key (request data). SPEC-UNVERIFIED variant.</summary>
    DataEncryption,
}
