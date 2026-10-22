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
                new IdentityBinding("a", "two") { Identity = identity with { MonitorDevicePath = "synthetic://two" } },
                new IdentityBinding("b", "one") { Identity = identity with { MonitorDevicePath = "synthetic://one" } },
            ],
        };

        var result = _resolver.Resolve(profile,
        [
            Connected("one", identity with { MonitorDevicePath = "synthetic://one" }),
            Connected("two", identity with { MonitorDevicePath = "synthetic://two" }),
        ]);

        Assert.Equal(IdentityResolutionStatus.Success, result.Status);
        Assert.Equal("two", result.Bindings.Single(binding => binding.DisplayId == "a").RuntimeId);
    }

    [Fact]
    public void LegacyRuntimeBindingCannotOverrideConflictingPhysicalSerial()
    {
        var profile = TestData.Profile() with
        {
            LastSuccessfulIdentityBindings = [new IdentityBinding("display-1", "reused-runtime")],
        };
        var wrongPanel = profile.Displays[0].Identity with { EdidSerial = "DIFFERENT-PANEL" };

        var result = _resolver.Resolve(profile, [Connected("reused-runtime", wrongPanel)]);

        Assert.Equal(IdentityResolutionStatus.Missing, result.Status);
        Assert.False(MonitorIdentityResolver.Score(profile.Displays[0].Identity, wrongPanel, rememberedBinding: true).MeetsThreshold);
    }

    [Fact]
    public void DurableBindingFollowsPanelWhenRuntimeAddressChanges()
    {
        var profile = TestData.Profile();
        var identity = profile.Displays[0].Identity;
        profile = profile with
        {
            LastSuccessfulIdentityBindings = [new IdentityBinding("display-1", "old-runtime") { Identity = identity }],
        };

        var result = _resolver.Resolve(profile, [Connected("new-runtime", identity)]);

        Assert.Equal(IdentityResolutionStatus.Success, result.Status);
        Assert.Equal("new-runtime", Assert.Single(result.Bindings).RuntimeId);
    }

    [Fact]
    public void ExplicitReplacementBindingUsesNewPhysicalBaseline()
    {
        var profile = TestData.Profile();
        var replacement = TestData.Identity("REPLACEMENT");
        profile = profile with
        {
            LastSuccessfulIdentityBindings = [new IdentityBinding("display-1", "selected") { Identity = replacement }],
        };

        Assert.Equal(IdentityResolutionStatus.Success, _resolver.Resolve(profile, [Connected("changed", replacement)]).Status);
        Assert.Equal(IdentityResolutionStatus.Missing, _resolver.Resolve(profile, [Connected("selected", profile.Displays[0].Identity)]).Status);
    }

    [Fact]
    public void LegacyRuntimeBindingDoesNotResolveIndistinguishablePanels()
    {
        var identity = TestData.Identity() with
        {
            MonitorDevicePath = null,
            DeviceInstanceId = null,
            EdidSerial = null,
            ConnectorInstance = null,
        };
        var profile = TestData.Profile(displays: [TestData.Display() with { Identity = identity }]) with
        {
            LastSuccessfulIdentityBindings = [new IdentityBinding("display-1", "one")],
        };

        Assert.Equal(IdentityResolutionStatus.Ambiguous,
            _resolver.Resolve(profile, [Connected("one", identity), Connected("two", identity)]).Status);
    }

    [Fact]
    public void ContainerConflictCannotBeOutweighedByPortOrModelSignals()
    {
        var saved = TestData.Identity() with { DeviceContainerId = "container-a", EdidSerial = null };
        var other = saved with { DeviceContainerId = "container-b" };

        Assert.False(MonitorIdentityResolver.Score(saved, other, rememberedBinding: true).MeetsThreshold);
        Assert.True(MonitorIdentityResolver.Score(saved with { EdidSerial = "stable" }, other with { EdidSerial = "stable" }).MeetsThreshold);
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
