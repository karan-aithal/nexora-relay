using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using OpenForecourt.Abstractions;
using OpenForecourt.Abstractions.Domain;
using OpenForecourt.Abstractions.Ports;

namespace OpenForecourt.Crypto.Tokenization;

/// <summary>
/// A deterministic, format-preserving token vault: the P2PE boundary object. The OPT calls
/// <see cref="TokenizeAsync"/> and everything downstream sees only the returned token.
/// </summary>
/// <remarks>
/// <para>
/// A token is the same length as its PAN, is all digits, passes Luhn, and keeps the real
/// last four for receipts. The middle digits are derived by HMAC-SHA256 under a per-instance
/// random key, so they reveal nothing about the PAN and cannot be reproduced without this
/// vault; detokenization is a lookup in a map held only inside the vault. Together that makes
/// the token irreversible to anyone downstream — the whole point of the boundary.
/// </para>
/// <para>
/// This is a simulation-grade vault (an in-memory map, an ephemeral key). A production vault
/// is an HSM-backed format-preserving encryption service; see the walkthrough's known
/// weaknesses. Only test PANs ever pass through it (CLAUDE.md §7).
/// </para>
/// </remarks>
public sealed class FpeTokenVault : ITokenVault
{
    private readonly byte[] _hmacKey = RandomNumberGenerator.GetBytes(32);
    private readonly ConcurrentDictionary<string, string> _tokenToPan = new(StringComparer.Ordinal);

    /// <inheritdoc />
    public Task<string> TokenizeAsync(Pan pan, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        string token = DeriveToken(pan.Reveal());
        _tokenToPan[token] = pan.Reveal();
        return Task.FromResult(token);
    }

    /// <inheritdoc />
    public Task<Result<Pan, TokenVaultError>> DetokenizeAsync(string token, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(_tokenToPan.TryGetValue(token, out string? pan)
            ? Result<Pan, TokenVaultError>.Ok(new Pan(pan))
            : Result<Pan, TokenVaultError>.Fail(TokenVaultError.UnknownToken));
    }

    private string DeriveToken(string pan)
    {
        int n = pan.Length;
        if (n <= 4)
        {
            // Degenerate: nothing to preserve or adjust. Test PANs are 15–16 digits.
            return pan;
        }

        // Keep the real last four; fill the rest with HMAC-derived pseudo-random digits.
        Span<char> token = stackalloc char[n];
        pan.AsSpan(n - 4).CopyTo(token[(n - 4)..]);

        byte[] mac = HMACSHA256.HashData(_hmacKey, Encoding.ASCII.GetBytes(pan));
        for (int i = 0; i < n - 4; i++)
        {
            token[i] = (char)('0' + mac[i] % 10);
        }

        // Nudge one middle digit so the whole token passes Luhn, leaving the last four intact.
        FixLuhn(token);
        return new string(token);
    }

    // Adjust the first middle digit until the Luhn checksum is zero (≤10 candidates). Only a
    // middle digit is touched, so the preserved last four never change.
    private static void FixLuhn(Span<char> token)
    {
        for (int candidate = 0; candidate < 10; candidate++)
        {
            token[0] = (char)('0' + candidate);
            if (Luhn.Checksum(token) == 0)
            {
                return;
            }
        }
    }
}
