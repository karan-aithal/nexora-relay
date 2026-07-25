using System.Collections.Concurrent;
using OpenForecourt.Abstractions;
using OpenForecourt.Abstractions.Ports;

namespace OpenForecourt.Crypto.KeyStore;

/// <summary>
/// An in-memory <see cref="ISecureKeyStore"/> for tests only. It refuses to construct in a
/// Release build (CLAUDE.md §3): unprotected key material in process memory must never be a
/// production path, and the compiler enforces that rather than a code review catching it.
/// </summary>
public sealed class InMemoryKeyStore : ISecureKeyStore
{
    private readonly ConcurrentDictionary<string, byte[]> _keys = new(StringComparer.Ordinal);

    /// <summary>Constructs the store. Throws in a Release build.</summary>
    /// <exception cref="InvalidOperationException">Thrown when compiled in Release.</exception>
    public InMemoryKeyStore()
    {
#if !DEBUG
        throw new InvalidOperationException(
            "InMemoryKeyStore is a test-only store and must not run in a Release build (CLAUDE.md §3). " +
            "Use DpapiKeyStore or another protected store in Release.");
#endif
    }

    /// <inheritdoc />
    public Task StoreAsync(string keyLabel, ReadOnlyMemory<byte> keyMaterial, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _keys[keyLabel] = keyMaterial.ToArray();
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task<Result<ReadOnlyMemory<byte>, KeyStoreError>> RetrieveAsync(string keyLabel, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(_keys.TryGetValue(keyLabel, out byte[]? key)
            ? Result<ReadOnlyMemory<byte>, KeyStoreError>.Ok(key)
            : Result<ReadOnlyMemory<byte>, KeyStoreError>.Fail(KeyStoreError.KeyNotFound));
    }

    /// <inheritdoc />
    public Task DeleteAsync(string keyLabel, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _keys.TryRemove(keyLabel, out _);
        return Task.CompletedTask;
    }
}
