using MoniTopo.Core.Identity;
using MoniTopo.Core.Models;

namespace MoniTopo.Core.Tests;

public sealed class MonitorIdentityResolverTests
{
    private readonly MonitorIdentityResolver _resolver = new();

    [Fact]
    public void ExactStrongIdentityResolves()
    {
        var profile = TestData.Profile();
        var candidate = Connected("runtime-1", profile.Displays[0].Identity);

        var result = _resolver.Resolve(profile, [candidate]);

        Assert.Equal(IdentityResolutionStatus.Success, result.Status);
        Assert.Equal("runtime-1", Assert.Single(result.Bindings).RuntimeId);
    }

    [Fact]
    public void ChangedDevicePathCanResolveThroughStableInstanceId()
    {
        var profile = TestData.Profile();
        var candidateIdentity = profile.Displays[0].Identity with { MonitorDevicePath = "synthetic://changed" };

        var result = _resolver.Resolve(profile, [Connected("runtime-1", candidateIdentity)]);

        Assert.Equal(IdentityResolutionStatus.Success, result.Status);
    }

    [Fact]
    public void SerialAbsentUsesUniqueCompositeFallback()
    {
        var savedIdentity = TestData.Identity() with
        {
            MonitorDevicePath = null,
            DeviceInstanceId = null,
            DeviceContainerId = null,
            EdidSerial = null,
        };
        var profile = TestData.Profile(displays: [TestData.Display() with { Identity = savedIdentity }]);
        var candidate = Connected("runtime-1", savedIdentity);

        Assert.Equal(IdentityResolutionStatus.Success, _resolver.Resolve(profile, [candidate]).Status);
    }

    [Fact]
    public void MaximumScoreAssignmentAvoidsGreedyCollision()
    {
        var savedA = TestData.Identity("A") with { DeviceInstanceId = "INSTANCE-A", EdidSerial = null };
        var savedB = TestData.Identity("B") with
        {
            MonitorDevicePath = null,
            DeviceInstanceId = null,
            EdidSerial = "SERIAL-B",
            FriendlyModelName = null,
            EdidManufacturerId = null,
            EdidProductCode = null,
            PreferredMode = null,
            SupportedModeSignature = null,
            OutputTechnology = DisplayOutputTechnology.Unknown,
            ConnectorInstance = null,
        };
        var candidateOne = savedA with { DeviceInstanceId = null, EdidSerial = "SERIAL-B" };
        var candidateTwo = savedA with { MonitorDevicePath = "different", DeviceInstanceId = "INSTANCE-A" };
        var displays = new[]
        {
            TestData.Display("a", primary: true) with { Identity = savedA },
            TestData.Display("b", primary: false) with { Identity = savedB },
        };
        var profile = TestData.Profile(displays: displays, primaryDisplayId: "a");

        var result = _resolver.Resolve(profile, [Connected("one", candidateOne), Connected("two", candidateTwo)]);

        Assert.Equal(IdentityResolutionStatus.Success, result.Status);
        Assert.Equal("two", result.Bindings.Single(binding => binding.DisplayId == "a").RuntimeId);
        Assert.Equal("one", result.Bindings.Single(binding => binding.DisplayId == "b").RuntimeId);
    }

    [Fact]
    public void IndistinguishableSameModelDisplaysAreAmbiguous()
    {
        var identity = TestData.Identity() with
        {
            MonitorDevicePath = null,
            DeviceInstanceId = null,
            DeviceContainerId = null,
            EdidSerial = null,
            ConnectorInstance = null,
        };
        var displays = new[]
        {
            TestData.Display("a", primary: true) with { Identity = identity },
            TestData.Display("b", primary: false) with { Identity = identity },
        };
        var profile = TestData.Profile(displays: displays, primaryDisplayId: "a");

        var result = _resolver.Resolve(profile, [Connected("one", identity), Connected("two", identity)]);

        Assert.Equal(IdentityResolutionStatus.Ambiguous, result.Status);
    }

    [Fact]
    public void RememberedBindingResolvesOtherwiseAmbiguousDisplays()
    {
        var identity = TestData.Identity() with
        {
            MonitorDevicePath = null,
            DeviceInstanceId = null,
            DeviceContainerId = null,
            EdidSerial = null,
            ConnectorInstance = null,
        };
        var displays = new[]
        {
            TestData.Display("a", primary: true) with { Identity = identity },
            TestData.Display("b", primary: false) with { Identity = identity },
        };
        var profile = TestData.Profile(displays: displays, primaryDisplayId: "a") with
        {
            LastSuccessfulIdentityBindings =
            [
                new IdentityBinding("a", "two"),
                new IdentityBinding("b", "one"),
            ],
        };

        var result = _resolver.Resolve(profile, [Connected("one", identity), Connected("two", identity)]);

        Assert.Equal(IdentityResolutionStatus.Success, result.Status);
        Assert.Equal("two", result.Bindings.Single(binding => binding.DisplayId == "a").RuntimeId);
    }

    [Fact]
    public void RequiredMissingDisplayIsNamed()
    {
        var profile = TestData.Profile(name: "Movie");

        var result = _resolver.Resolve(profile, Array.Empty<ConnectedDisplayState>());

        Assert.Equal(IdentityResolutionStatus.Missing, result.Status);
        Assert.Equal(profile.Displays[0].FriendlyLabel, result.ProblemDisplayLabel);
    }

    private static ConnectedDisplayState Connected(string runtimeId, MonitorIdentityFingerprint identity) =>
        new(runtimeId, identity, false, runtimeId, null);
}
