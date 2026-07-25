using OpenForecourt.SiteController.Config;

namespace OpenForecourt.SiteController.Orchestration;

/// <summary>
/// The offline authorisation gate: enforces the per-transaction floor limit and the total
/// offline exposure ceiling (CLAUDE.md Phase 5, "Configurable offline transaction ceiling
/// and total exposure limit").
/// </summary>
/// <remarks>
/// Exposure is the sum of offline authorisations that have not yet been replayed to the
/// host. It is reserved when an offline authorisation is granted and released when that
/// transaction is later replayed (approved or reversed). On startup it is rebuilt from the
/// journal via <see cref="Restore"/> so a crash does not lose track of what the site is
/// carrying.
/// </remarks>
public sealed class OfflinePolicy(SiteOptions options)
{
    private readonly Lock _gate = new();
    private long _exposure;

    /// <summary>The current total offline exposure, in minor units.</summary>
    public long CurrentExposure
    {
        get { lock (_gate) { return _exposure; } }
    }

    /// <summary>
    /// Attempts to reserve an offline authorisation. Succeeds only when the amount is at or
    /// below the floor limit and the new exposure stays within the ceiling.
    /// </summary>
    public bool TryReserve(long amountMinor)
    {
        if (amountMinor > options.FloorLimitMinor)
        {
            return false;
        }

        lock (_gate)
        {
            if (_exposure + amountMinor > options.OfflineExposureCeilingMinor)
            {
                return false;
            }

            _exposure += amountMinor;
            return true;
        }
    }

    /// <summary>Releases exposure once an offline transaction has been replayed.</summary>
    public void Release(long amountMinor)
    {
        lock (_gate)
        {
            _exposure = Math.Max(0, _exposure - amountMinor);
        }
    }

    /// <summary>Rebuilds exposure at startup from an unreplayed offline authorisation found in the journal.</summary>
    public void Restore(long amountMinor)
    {
        lock (_gate)
        {
            _exposure += amountMinor;
        }
    }
}
