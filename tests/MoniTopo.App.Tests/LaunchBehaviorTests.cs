using MoniTopo.App.Lifecycle;

namespace MoniTopo.App.Tests;

public sealed class LaunchBehaviorTests
{
    [Theory]
    [InlineData(false, false, false, false)]
    [InlineData(false, true, false, true)]
    [InlineData(false, true, true, false)]
    [InlineData(true, false, true, true)]
    public void BackgroundStartupHonorsFirstRunButSuppressesSavedWindowPreference(bool firstRun, bool showWindow, bool background, bool expected)
    {
        Assert.Equal(expected, LaunchBehavior.ShouldShowMainWindow(background ? ["--background"] : [], firstRun, showWindow));
    }
}
