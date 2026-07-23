using HoloFan.Web.Services;

namespace HoloFan.Tests;

public class FanSyncTests
{
    [Fact]
    public void Nothing_is_lost_when_the_target_covers_the_card()
    {
        var lost = FanSyncService.ClipsLost(
            currentFanNames: new[] { "Tiger", "Mario" },
            targetNames: new[] { "Tiger", "Mario", "New Clip" });
        Assert.Empty(lost);
    }

    [Fact]
    public void Reports_card_clips_missing_from_the_target()
    {
        var lost = FanSyncService.ClipsLost(
            currentFanNames: new[] { "Tiger", "Mario", "Watermelon" },
            targetNames: new[] { "Tiger" });
        Assert.Equal(new[] { "Mario", "Watermelon" }, lost);
    }

    [Fact]
    public void Name_match_is_case_insensitive()
    {
        var lost = FanSyncService.ClipsLost(
            currentFanNames: new[] { "TIGER" },
            targetNames: new[] { "tiger" });
        Assert.Empty(lost);
    }

    [Fact]
    public void Everything_is_lost_when_clearing_the_fan()
    {
        var lost = FanSyncService.ClipsLost(
            currentFanNames: new[] { "A", "B" },
            targetNames: Array.Empty<string>());
        Assert.Equal(new[] { "A", "B" }, lost);
    }
}
