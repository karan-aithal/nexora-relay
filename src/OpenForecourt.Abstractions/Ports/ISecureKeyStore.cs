namespace OpenForecourt.Abstractions.Ports;

/// <summary>
/// Protected storage for cryptographic key material (BDKs, IPEKs, and derived keys used
/// by DUKPT and PIN processing in later phases).
/// </summary>
/// <remarks>
/// <para>
/// Implementations include a Windows DPAPI-backed store (keys encrypted at rest by the OS
/// user/machine key) and an in-memory store used only by tests — the in-memory store must
/// refuse to run in a Release build (CLAUDE.md section 3).
/// </para>
/// <para>
/// Key material is handled as raw bytes and is never logged. Keys are addressed by an
/// opaque string label; the store does not interpret the bytes. Only test keys ever pass
/// through this interface in this repository (CLAUDE.md section 7).
/// </para>
/// </remarks>
public interface ISecureKeyStore
{
    /// <summary>Stores (or replaces) key material under a label.</summary>
    /// <param name="keyLabel">The opaque identifier for the key.</param>
    /// <param name="keyMaterial">The raw key bytes to protect.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    Task StoreAsync(string keyLabel, ReadOnlyMemory<byte> keyMaterial, CancellationToken cancellationToken);

    /// <summary>Retrieves key material by label.</summary>
    /// <param name="keyLabel">The identifier used when the key was stored.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>The key bytes, or a <see cref="KeyStoreError"/> when the label is unknown.</returns>
    Task<Result<ReadOnlyMemory<byte>, KeyStoreError>> RetrieveAsync(string keyLabel, CancellationToken cancellationToken);

    /// <summary>Removes key material by label. Idempotent.</summary>
    /// <param name="keyLabel">The identifier to remove.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    Task DeleteAsync(string keyLabel, CancellationToken cancellationToken);
}

/// <summary>Ways a key-store lookup can fail.</summary>
public enum KeyStoreError
{
    /// <summary>No key exists under the requested label.</summary>
    KeyNotFound,
}
