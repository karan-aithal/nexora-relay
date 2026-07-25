using System.Runtime.Versioning;
using System.Security.Cryptography;
using OpenForecourt.Abstractions;
using OpenForecourt.Abstractions.Ports;

namespace OpenForecourt.Crypto.KeyStore;

/// <summary>
/// A Windows DPAPI-backed <see cref="ISecureKeyStore"/>: key material is encrypted at rest
/// under the OS user/machine key and written to a directory, one protected blob per label.
/// This is the real production-shaped path on Windows (the counterpart to the physical PC/SC
/// reader); CI runs Linux and uses <see cref="InMemoryKeyStore"/> instead (CLAUDE.md §2).
/// </summary>
/// <remarks>
/// DPAPI is not a substitute for a hardware security module — a real Secure Cryptographic
/// Device holds keys in tamper-responsive hardware and never exports them. This store makes
/// keys unreadable by other OS users/machines, which is the appropriate simulation-grade
/// protection; the threat model states plainly what it does and does not prove.
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed class DpapiKeyStore : ISecureKeyStore
{
    private readonly string _directory;
    private readonly DataProtectionScope _scope;

    /// <summary>Creates the store over a directory, creating it if needed.</summary>
    /// <param name="directory">Where protected key blobs are written.</param>
    /// <param name="scope">DPAPI scope; CurrentUser by default.</param>
    public DpapiKeyStore(string directory, DataProtectionScope scope = DataProtectionScope.CurrentUser)
    {
        _directory = directory;
        _scope = scope;
        Directory.CreateDirectory(directory);
    }

    /// <inheritdoc />
    public async Task StoreAsync(string keyLabel, ReadOnlyMemory<byte> keyMaterial, CancellationToken cancellationToken)
    {
        byte[] protectedBytes = ProtectedData.Protect(keyMaterial.ToArray(), optionalEntropy: null, _scope);
        await File.WriteAllBytesAsync(PathFor(keyLabel), protectedBytes, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<Result<ReadOnlyMemory<byte>, KeyStoreError>> RetrieveAsync(string keyLabel, CancellationToken cancellationToken)
    {
        string path = PathFor(keyLabel);
        if (!File.Exists(path))
        {
            return Result<ReadOnlyMemory<byte>, KeyStoreError>.Fail(KeyStoreError.KeyNotFound);
        }

        byte[] protectedBytes = await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);
        byte[] clear = ProtectedData.Unprotect(protectedBytes, optionalEntropy: null, _scope);
        return Result<ReadOnlyMemory<byte>, KeyStoreError>.Ok(clear);
    }

    /// <inheritdoc />
    public Task DeleteAsync(string keyLabel, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        string path = PathFor(keyLabel);
        if (File.Exists(path))
        {
            File.Delete(path);
        }

        return Task.CompletedTask;
    }

    // Label → file name. The label is an opaque identifier; hash it to a safe file name so
    // arbitrary label characters cannot escape the directory.
    private string PathFor(string keyLabel)
    {
        byte[] hash = SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(keyLabel));
        return Path.Combine(_directory, Convert.ToHexString(hash) + ".dpapi");
    }
}
