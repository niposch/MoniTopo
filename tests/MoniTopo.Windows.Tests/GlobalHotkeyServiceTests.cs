using MoniTopo.Core.Models;
using MoniTopo.Windows.Input;

namespace MoniTopo.Windows.Tests;

public sealed class GlobalHotkeyServiceTests
{
    [Fact]
    public void UnmodifiedKeyIsRegisteredWithoutModifiersButWithNoRepeat()
    {
        var native = new FakeHotkeyNative();
        using var service = new GlobalHotkeyService((nint)42, native);
        Assert.True(service.RegisterProfile(Guid.NewGuid(), new HotkeyBinding(HotkeyModifiers.None, 0x77)).Succeeded);
        Assert.Equal(0x4000u, Assert.Single(native.RegisterCalls).Modifiers);
    }
    [Fact]
    public void RegistersNoRepeatAndRoutesWindowMessage()
    {
        var native = new FakeHotkeyNative();
        using var service = new GlobalHotkeyService((nint)42, native);
        HotkeyCommand? pressed = null;
        service.Pressed += (_, command) => pressed = command;

        var result = service.RegisterPopup(new HotkeyBinding(HotkeyModifiers.Control | HotkeyModifiers.Alt, 0x4D));
        var handled = service.ProcessWindowMessage(0x0312, (nint)GlobalHotkeyService.PopupHotkeyId);

        Assert.True(result.Succeeded);
        Assert.True(handled);
        Assert.Equal(HotkeyCommandKind.TogglePopup, pressed?.Kind);
        Assert.Equal(0x4003u, Assert.Single(native.RegisterCalls).Modifiers);
    }

    [Fact]
    public void FailedReplacementRestoresPreviousWorkingBinding()
    {
        var native = new FakeHotkeyNative();
        using var service = new GlobalHotkeyService((nint)42, native);
        var original = new HotkeyBinding(HotkeyModifiers.Control, 0x31);
        var replacement = new HotkeyBinding(HotkeyModifiers.Control, 0x32);
        Assert.True(service.RegisterPopup(original).Succeeded);
        native.RejectedVirtualKey = 0x32;

        var result = service.RegisterPopup(replacement);

        Assert.False(result.Succeeded);
        Assert.True(result.PreviousBindingRetained);
        Assert.Equal([0x31u, 0x32u, 0x31u], native.RegisterCalls.Select(call => call.VirtualKey));
    }

    [Fact]
    public void RejectsInternalConflictBeforeCallingWindows()
    {
        var native = new FakeHotkeyNative();
        using var service = new GlobalHotkeyService((nint)42, native);
        var binding = new HotkeyBinding(HotkeyModifiers.Alt, 0x4D);
        Assert.True(service.RegisterPopup(binding).Succeeded);

        var result = service.RegisterProfile(Guid.NewGuid(), binding);

        Assert.False(result.Succeeded);
        Assert.Contains("already assigned", result.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Single(native.RegisterCalls);
    }

    [Fact]
    public void ProfileHotkeysAreUnregisteredDuringActivationAndRestoredAfterward()
    {
        var native = new FakeHotkeyNative();
        using var service = new GlobalHotkeyService((nint)42, native);
        var profileId = Guid.NewGuid();
        Assert.True(service.RegisterProfile(profileId, new HotkeyBinding(HotkeyModifiers.Control, 0x32)).Succeeded);

        service.SetProfileHotkeysEnabled(false);
        var handledWhileDisabled = service.ProcessWindowMessage(0x0312, (nint)2);
        var results = service.SetProfileHotkeysEnabled(true);

        Assert.False(handledWhileDisabled);
        Assert.All(results, result => Assert.True(result.Succeeded));
        Assert.Contains(2, native.UnregisterCalls);
        Assert.Equal(2, native.RegisterCalls.Count);
    }

    [Fact]
    public void DisposeUnregistersEveryWorkingBinding()
    {
        var native = new FakeHotkeyNative();
        var service = new GlobalHotkeyService((nint)42, native);
        service.RegisterPopup(new HotkeyBinding(HotkeyModifiers.Control, 0x31));
        service.RegisterProfile(Guid.NewGuid(), new HotkeyBinding(HotkeyModifiers.Control, 0x32));

        service.Dispose();

        Assert.Equal([1, 2], native.UnregisterCalls.Order());
    }

    [Fact]
    public void FailedReregistrationCanBeRetriedWithTheSameBinding()
    {
        var native = new FakeHotkeyNative();
        using var service = new GlobalHotkeyService((nint)42, native);
        var id = Guid.NewGuid();
        var binding = new HotkeyBinding(HotkeyModifiers.Control, 0x32);
        service.RegisterProfile(id, binding);
        service.SetProfileHotkeysEnabled(false);
        native.RejectedVirtualKey = 0x32;
        Assert.False(Assert.Single(service.SetProfileHotkeysEnabled(true)).Succeeded);
        native.RejectedVirtualKey = null;

        Assert.True(service.RegisterProfile(id, binding).Succeeded);
        Assert.True(service.ProcessWindowMessage(0x0312, (nint)2));
    }

    [Fact]
    public void WrongThreadRegistrationIsRejectedBeforeCallingNativeCode()
    {
        var native = new FakeHotkeyNative();
        using var service = new GlobalHotkeyService((nint)42, native);
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            failure = Record.Exception(() => service.RegisterPopup(new HotkeyBinding(HotkeyModifiers.Control, 0x32)));
        });
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(5)));

        Assert.IsType<InvalidOperationException>(failure);
        Assert.Empty(native.RegisterCalls);
    }

    private sealed class FakeHotkeyNative : IGlobalHotkeyNative
    {
        internal List<(int Id, uint Modifiers, uint VirtualKey)> RegisterCalls { get; } = [];

        internal List<int> UnregisterCalls { get; } = [];

        internal uint? RejectedVirtualKey { get; set; }

        public bool Register(nint windowHandle, int id, uint modifiers, uint virtualKey)
        {
            RegisterCalls.Add((id, modifiers, virtualKey));
            return virtualKey != RejectedVirtualKey;
        }

        public bool Unregister(nint windowHandle, int id)
        {
            UnregisterCalls.Add(id);
            return true;
        }
    }

    [Fact]
    public void ReplacementWhileSuppressedStillChecksWindowsAndRetainsOldBindingOnConflict()
    {
        var native = new FakeHotkeyNative();
        using var service = new GlobalHotkeyService((nint)42, native);
        var profile = Guid.NewGuid();
        service.RegisterProfile(profile, new HotkeyBinding(HotkeyModifiers.Control, 0x31));
        service.SetProfileHotkeysEnabled(false);
        native.RejectedVirtualKey = 0x32;

        var result = service.RegisterProfile(profile, new HotkeyBinding(HotkeyModifiers.Control, 0x32));

        Assert.False(result.Succeeded);
        Assert.True(Assert.Single(service.SetProfileHotkeysEnabled(true)).Succeeded);
        Assert.Equal(new uint[] { 0x31, 0x32, 0x31 }, native.RegisterCalls.Select(call => call.VirtualKey));
    }

    [Fact]
    public void AcceptedReplacementProbeDoesNotEnableHotkeyUntilInteractionEnds()
    {
        var native = new FakeHotkeyNative();
        using var service = new GlobalHotkeyService((nint)42, native);
        var profile = Guid.NewGuid();
        service.RegisterProfile(profile, new HotkeyBinding(HotkeyModifiers.Control, 0x31));
        service.SetProfileHotkeysEnabled(false);

        Assert.True(service.RegisterProfile(profile, new HotkeyBinding(HotkeyModifiers.Control, 0x32)).Succeeded);
        Assert.False(service.ProcessWindowMessage(0x0312, (nint)2));
        Assert.True(Assert.Single(service.SetProfileHotkeysEnabled(true)).Succeeded);
        Assert.True(service.ProcessWindowMessage(0x0312, (nint)2));
        Assert.Equal(new uint[] { 0x31, 0x32, 0x32 }, native.RegisterCalls.Select(call => call.VirtualKey));
    }
}
