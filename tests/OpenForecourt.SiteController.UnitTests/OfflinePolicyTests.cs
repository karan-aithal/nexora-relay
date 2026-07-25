using OpenForecourt.SiteController.Config;
using OpenForecourt.SiteController.Orchestration;
using Xunit;

namespace OpenForecourt.SiteController.UnitTests;

public sealed class OfflinePolicyTests
{
    private static OfflinePolicy Policy(long floor, long ceiling) =>
        new(new SiteOptions { FloorLimitMinor = floor, OfflineExposureCeilingMinor = ceiling });

    [Fact]
    public void Reserve_rejects_amount_over_floor_limit()
    {
        var policy = Policy(floor: 5000, ceiling: 100000);
        Assert.False(policy.TryReserve(5001));
        Assert.True(policy.TryReserve(5000));
    }

    [Fact]
    public void Reserve_rejects_when_exposure_ceiling_would_be_exceeded()
    {
        var policy = Policy(floor: 5000, ceiling: 12000);
        Assert.True(policy.TryReserve(5000));
        Assert.True(policy.TryReserve(5000));
        Assert.False(policy.TryReserve(5000)); // 15000 > 12000
        Assert.Equal(10000, policy.CurrentExposure);
    }

    [Fact]
    public void Release_frees_exposure_for_further_reservations()
    {
        var policy = Policy(floor: 5000, ceiling: 10000);
        Assert.True(policy.TryReserve(5000));
        Assert.True(policy.TryReserve(5000));
        Assert.False(policy.TryReserve(5000));
        policy.Release(5000);
        Assert.Equal(5000, policy.CurrentExposure);
        Assert.True(policy.TryReserve(5000));
    }
}
