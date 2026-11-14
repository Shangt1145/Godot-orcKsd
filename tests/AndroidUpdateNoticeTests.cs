using Kards.Ui.OrcBridge.Updates;
using Xunit;

namespace Kards.Ui.Tests;

public class AndroidUpdateNoticeTests
{
    private static ReleaseInfo Release(string version, bool draft = false, string? apkVersion = null) =>
        new("v" + version, version, draft, "更新说明",
            [new($"OrC-KSD-{apkVersion ?? version}-android.apk", 100, "https://example.test/game.apk")]);

    [Fact]
    public void PicksNewestAndroidReleaseUsingNumericBetaOrder()
    {
        var selected = ReleaseFeed.SelectAndroidNotice(
            [Release("2.0.0-beta.9"), Release("2.0.0-beta.12"), Release("2.0.0-beta.10")], "2.0.0-beta.8");
        Assert.Equal("2.0.0-beta.12", selected?.Version);
    }

    [Fact]
    public void IgnoresDraftDesktopOnlyAndApkFromAnotherVersion()
    {
        ReleaseInfo desktop = new("v2.0.0-beta.15", "2.0.0-beta.15", false, "",
            [new("OrC-KSD-2.0.0-beta.15-win-x64.zip", 100, "https://example.test/game.zip")]);
        var selected = ReleaseFeed.SelectAndroidNotice(
            [Release("2.0.0-beta.11"), Release("2.0.0-beta.13", draft: true), desktop,
                Release("2.0.0-beta.14", apkVersion: "2.0.0-beta.10")], "2.0.0-beta.10");
        Assert.Equal("2.0.0-beta.11", selected?.Version);
    }

    [Theory]
    [InlineData("2.0.0-beta.11")]
    [InlineData("2.0.0-beta.12")]
    [InlineData("")]
    public void DoesNotNotifySameOlderOrUnknownCurrentVersion(string current)
    {
        Assert.Null(ReleaseFeed.SelectAndroidNotice([Release("2.0.0-beta.11")], current));
    }
}
