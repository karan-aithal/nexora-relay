using OpenForecourt.Abstractions.Ports;
using OpenForecourt.Crypto.KeyStore;
using Xunit;

namespace OpenForecourt.Crypto.UnitTests;

public sealed class KeyStoreTests
{
#if DEBUG
    [Fact]
    public async Task InMemory_store_round_trips_and_deletes()
    {
        var store = new InMemoryKeyStore();
        byte[] key = Convert.FromHexString("0123456789ABCDEFFEDCBA9876543210");
        await store.StoreAsync("bdk-test", key, CancellationToken.None);

        var got = await store.RetrieveAsync("bdk-test", CancellationToken.None);
        Assert.True(got.IsSuccess);
        Assert.Equal(key, got.Value.ToArray());

        await store.DeleteAsync("bdk-test", CancellationToken.None);
        var gone = await store.RetrieveAsync("bdk-test", CancellationToken.None);
        Assert.True(gone.IsError);
        Assert.Equal(KeyStoreError.KeyNotFound, gone.Error);
    }

    [Fact]
    public async Task InMemory_unknown_label_is_key_not_found()
    {
        var store = new InMemoryKeyStore();
        var got = await store.RetrieveAsync("nope", CancellationToken.None);
        Assert.True(got.IsError);
        Assert.Equal(KeyStoreError.KeyNotFound, got.Error);
    }
#else
    [Fact]
    public void InMemory_store_refuses_to_construct_in_release()
    {
        // CLAUDE.md §3: unprotected in-memory keys must never be a Release path.
        Assert.Throws<InvalidOperationException>(() => new InMemoryKeyStore());
    }
#endif
}
