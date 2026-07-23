using OpenForecourt.Abstractions.Domain;

namespace OpenForecourt.Abstractions.Ports;

/// <summary>
/// Exchanges a cleartext <see cref="Pan"/> for an opaque surrogate token, and back.
/// </summary>
/// <remarks>
/// <para>
/// This is the architectural heart of the P2PE story (CLAUDE.md section 7.4): the OPT
/// tokenizes the PAN at the boundary, and every downstream component sees only the token.
/// The mapping from token to PAN lives on one side of that boundary only.
/// </para>
/// <para>
/// Tokens are stable per PAN within a vault instance (so the same card yields the same
/// token) but must reveal nothing about the PAN. Detokenization is the sensitive
/// operation and is expected to be available only inside the OPT trust boundary.
/// </para>
/// </remarks>
public interface ITokenVault
{
    /// <summary>Returns the surrogate token for a PAN, creating one if none exists.</summary>
    /// <param name="pan">The cleartext PAN to protect.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>An opaque token safe to pass to downstream components.</returns>
    Task<string> TokenizeAsync(Pan pan, CancellationToken cancellationToken);

    /// <summary>Recovers the PAN for a previously issued token.</summary>
    /// <param name="token">A token returned by <see cref="TokenizeAsync"/>.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>The PAN, or a <see cref="TokenVaultError"/> when the token is unknown.</returns>
    Task<Result<Pan, TokenVaultError>> DetokenizeAsync(string token, CancellationToken cancellationToken);
}

/// <summary>Ways a detokenization can fail.</summary>
public enum TokenVaultError
{
    /// <summary>The token is not present in the vault.</summary>
    UnknownToken,
}
