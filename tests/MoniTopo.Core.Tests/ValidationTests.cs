using MoniTopo.Core.Configuration;
using MoniTopo.Core.Models;
using MoniTopo.Core.Validation;

namespace MoniTopo.Core.Tests;

public sealed class ValidationTests
{
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void EmptyProfileNamesAreRejected(string name)
    {
        Assert.Contains(ProfileValidator.Validate(TestData.Profile(name)), error => error.Code == "profile.name.length");
    }

    [Fact]
    public void SixtyFourCharacterProfileNameIsAccepted()
    {
        Assert.Empty(ProfileValidator.Validate(TestData.Profile(new string('x', 64))));
    }

    [Fact]
    public void InvalidPrimaryDisplayIsRejected()
    {
        var profile = TestData.Profile() with { PrimaryDisplayId = "missing" };

        Assert.Contains(ProfileValidator.Validate(profile), error => error.Code == "profile.primary.invalid");
    }

    [Fact]
    public void DuplicateDisplayIdsAreRejectedCaseInsensitively()
    {
        var profile = TestData.Profile(
            displays:
            [
                TestData.Display("Panel", primary: true),
                TestData.Display("panel", primary: false),
            ],
            primaryDisplayId: "Panel");

        Assert.Contains(ProfileValidator.Validate(profile), error => error.Code == "profile.display.duplicate");
    }

    [Fact]
    public void DuplicateProfileNamesAreRejectedCaseInsensitively()
    {
        var first = TestData.Profile("Movie");
        var second = TestData.Profile(" movie ", Guid.NewGuid());

        Assert.Contains(
            ConfigurationValidator.Validate(TestData.Configuration(first, second)),
            error => error.Code == "profile.name.duplicate");
    }

    [Fact]
    public void HotkeyConflictIncludesPopupAndDirectBindings()
    {
        var profile = TestData.Profile(
            hotkey: new HotkeyBinding(HotkeyModifiers.Control | HotkeyModifiers.Alt, 0x4D));

        Assert.Contains(
            ConfigurationValidator.Validate(TestData.Configuration(profile)),
            error => error.Code == "hotkey.conflict");
    }

    [Fact]
    public void ProfileOrderMustContainEveryProfileExactlyOnce()
    {
        var profile = TestData.Profile();
        var configuration = TestData.Configuration(profile) with { ProfileOrder = Array.Empty<Guid>() };

        Assert.Contains(
            ConfigurationValidator.Validate(configuration),
            error => error.Code == "profile.order.invalid");
    }

    [Fact]
    public void DefaultConfigurationIsValid()
    {
        Assert.Empty(ConfigurationValidator.Validate(ApplicationConfiguration.CreateDefault()));
    }
}
