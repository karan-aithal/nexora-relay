using System.Collections.Concurrent;
using OpenForecourt.SiteController.Orchestration;

namespace OpenForecourt.SiteController.Api;

/// <summary>
/// De-duplicates state-changing requests by their idempotency key, so a retried authorisation
/// yields the original transaction rather than a second one (CLAUDE.md Phase 5, "idempotency
/// keys on every state-changing endpoint").
/// </summary>
/// <remarks>
/// Concurrent requests with the same key share a single execution via a cached
/// <see cref="Lazy{T}"/> of the in-flight task, so even simultaneous retries authorise once.
/// ponytail: results are kept for the process lifetime in memory — fine for a single site
/// controller; add TTL eviction or a shared store if this ever runs multi-instance.
/// </remarks>
public sealed class IdempotencyStore
{
    private readonly ConcurrentDictionary<string, Lazy<Task<AuthorisationResult>>> _results = new();

    /// <summary>Runs <paramref name="factory"/> once per key; repeat keys return the first result.</summary>
    public Task<AuthorisationResult> GetOrAddAsync(string key, Func<Task<AuthorisationResult>> factory) =>
        _results.GetOrAdd(key, _ => new Lazy<Task<AuthorisationResult>>(factory)).Value;
}
