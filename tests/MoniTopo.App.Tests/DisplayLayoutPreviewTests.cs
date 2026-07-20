using MoniTopo.App.Profiles;
using MoniTopo.Core.Models;

namespace MoniTopo.App.Tests;

public sealed class DisplayLayoutPreviewTests
{
    [Fact]
    public void PreviewPreservesRelativeGeometryAndPrimaryIndicator()
    {
        var left = Display("left", -1920, 0, 1920, 1080, primary: false);
        var primary = Display("primary", 0, 0, 2560, 1440, primary: true);
        var profile = Profile([left, primary], primary.DisplayId);

        var preview = DisplayLayoutPreview.Build(profile, 600, 280);

        Assert.Equal(2, preview.Count);
        Assert.True(preview.Single(item => item.DisplayId == primary.DisplayId).IsPrimary);
        Assert.True(preview.Single(item => item.DisplayId == left.DisplayId).Left < preview.Single(item => item.DisplayId == primary.DisplayId).Left);
        Assert.All(preview, item => Assert.True(item.Width > 0 && item.Height > 0));
    }

    [Fact]
    public void CloneGroupUsesSmallStackOffset()
    {
        var first = Display("one", 0, 0, 1920, 1080, true) with { CloneGroupId = "clone-a" };
        var second = Display("two", 0, 0, 1920, 1080, false) with { CloneGroupId = "clone-a", SourceGroupId = first.SourceGroupId };

        var preview = DisplayLayoutPreview.Build(Profile([first, second], first.DisplayId), 400, 240);

        Assert.Equal(5, preview[1].Left - preview[0].Left);
        Assert.Equal(5, preview[1].Top - preview[0].Top);
    }

    private static DesiredDisplayPath Display(string id, int x, int y, int width, int height, bool primary) => new(
        id,
        new MonitorIdentityFingerprint($"synthetic://{id}", null, null, id, "TST", 1, id, null, null, DisplayOutputTechnology.Hdmi, 1, new DisplaySize(width, height), null),
        $"source-{id}",
        null,
        new DisplayPoint(x, y),
        new DisplaySize(width, height),
        new RefreshRate(60, 1),
        DisplayOrientation.Landscape,
        DisplayPathScaling.Identity,
        100,
        false,
        primary,
        id);

    private static DisplayProfile Profile(IReadOnlyList<DesiredDisplayPath> displays, string primaryId) => new(
        Guid.NewGuid(),
        "Layout",
        DateTimeOffset.UnixEpoch,
        DateTimeOffset.UnixEpoch,
        null,
        displays,
        primaryId,
        1,
        []);
}
