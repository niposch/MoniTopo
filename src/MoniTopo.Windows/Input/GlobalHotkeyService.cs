using System.Runtime.InteropServices;
using MoniTopo.Core.Models;

namespace MoniTopo.Windows.Input;

public enum HotkeyCommandKind
{
    TogglePopup,
    ActivateProfile,
}

public sealed record HotkeyCommand(HotkeyCommandKind Kind, Guid? ProfileId = null);

public sealed record HotkeyRegistrationResult(
    bool Succeeded,
    string? Message,
    bool PreviousBindingRetained);

internal interface IGlobalHotkeyNative
{
    bool Register(nint windowHandle, int id, uint modifiers, uint virtualKey);

    bool Unregister(nint windowHandle, int id);
}

internal sealed class User32GlobalHotkeyNative : IGlobalHotkeyNative
{
    public bool Register(nint windowHandle, int id, uint modifiers, uint virtualKey) =>
        HotkeyNativeMethods.RegisterHotKey(windowHandle, id, modifiers, virtualKey);

    public bool Unregister(nint windowHandle, int id) =>
        HotkeyNativeMethods.UnregisterHotKey(windowHandle, id);
}

internal static partial class HotkeyNativeMethods
{
    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool RegisterHotKey(nint windowHandle, int id, uint modifiers, uint virtualKey);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool UnregisterHotKey(nint windowHandle, int id);
}

public sealed class GlobalHotkeyService : IDisposable
{
    public const int PopupHotkeyId = 1;
    private const uint NoRepeat = 0x4000;
    private readonly nint _windowHandle;
    private readonly IGlobalHotkeyNative _native;
    private readonly Dictionary<int, Registration> _registrations = [];
    private readonly Dictionary<Guid, int> _profileIds = [];
    private int _nextId = 2;
    private bool _profileHotkeysEnabled = true;
    private bool _disposed;

    public GlobalHotkeyService(nint windowHandle)
        : this(windowHandle, new User32GlobalHotkeyNative())
    {
    }

    internal GlobalHotkeyService(nint windowHandle, IGlobalHotkeyNative native)
    {
        if (windowHandle == nint.Zero)
        {
            throw new ArgumentException("A message window handle is required.", nameof(windowHandle));
        }

        _windowHandle = windowHandle;
        _native = native;
    }

    public event EventHandler<HotkeyCommand>? Pressed;

    public HotkeyRegistrationResult RegisterPopup(HotkeyBinding binding) =>
        Replace(PopupHotkeyId, new HotkeyCommand(HotkeyCommandKind.TogglePopup), binding, shouldRegister: true);

    public HotkeyRegistrationResult RegisterProfile(Guid profileId, HotkeyBinding? binding)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!_profileIds.TryGetValue(profileId, out var id))
        {
            id = AllocateId();
            _profileIds.Add(profileId, id);
        }

        if (binding is null)
        {
            RemoveRegistration(id);
            return new HotkeyRegistrationResult(true, null, false);
        }

        return Replace(
            id,
            new HotkeyCommand(HotkeyCommandKind.ActivateProfile, profileId),
            binding,
            _profileHotkeysEnabled);
    }

    public IReadOnlyList<HotkeyRegistrationResult> SetProfileHotkeysEnabled(bool enabled)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_profileHotkeysEnabled == enabled)
        {
            return [];
        }

        _profileHotkeysEnabled = enabled;
        var results = new List<HotkeyRegistrationResult>();
        foreach (var (id, registration) in _registrations.Where(item => item.Value.Command.Kind == HotkeyCommandKind.ActivateProfile).ToArray())
        {
            if (!enabled)
            {
                if (registration.IsRegistered)
                {
                    _native.Unregister(_windowHandle, id);
                    _registrations[id] = registration with { IsRegistered = false };
                }

                continue;
            }

            var succeeded = TryRegister(id, registration.Binding);
            _registrations[id] = registration with { IsRegistered = succeeded };
            results.Add(succeeded
                ? new HotkeyRegistrationResult(true, null, false)
                : ConflictResult(previousBindingRetained: false));
        }

        return results;
    }

    public bool ProcessWindowMessage(int message, nint wParam)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        const int WmHotkey = 0x0312;
        if (message != WmHotkey || !_registrations.TryGetValue(wParam.ToInt32(), out var registration) || !registration.IsRegistered)
        {
            return false;
        }

        Pressed?.Invoke(this, registration.Command);
        return true;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        foreach (var (id, registration) in _registrations)
        {
            if (registration.IsRegistered)
            {
                _native.Unregister(_windowHandle, id);
            }
        }

        _registrations.Clear();
        _profileIds.Clear();
        _disposed = true;
    }

    private HotkeyRegistrationResult Replace(
        int id,
        HotkeyCommand command,
        HotkeyBinding binding,
        bool shouldRegister)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_registrations.TryGetValue(id, out var previous) && previous.Binding == binding)
        {
            return new HotkeyRegistrationResult(true, null, previous.IsRegistered);
        }

        if (_registrations.Values.Any(registration => registration.Binding == binding && registration.Command != command))
        {
            return new HotkeyRegistrationResult(
                false,
                "That hotkey is already assigned to another MoniTopo action.",
                previous?.IsRegistered == true);
        }

        if (previous?.IsRegistered == true)
        {
            _native.Unregister(_windowHandle, id);
        }

        if (!shouldRegister)
        {
            _registrations[id] = new Registration(command, binding, false);
            return new HotkeyRegistrationResult(true, null, false);
        }

        if (TryRegister(id, binding))
        {
            _registrations[id] = new Registration(command, binding, true);
            return new HotkeyRegistrationResult(true, null, false);
        }

        var retained = previous is not null && TryRegister(id, previous.Binding);
        if (previous is not null)
        {
            _registrations[id] = previous with { IsRegistered = retained };
        }

        return ConflictResult(retained);
    }

    private bool TryRegister(int id, HotkeyBinding binding) => _native.Register(
        _windowHandle,
        id,
        checked((uint)binding.Modifiers) | NoRepeat,
        checked((uint)binding.VirtualKey));

    private void RemoveRegistration(int id)
    {
        if (_registrations.Remove(id, out var existing) && existing.IsRegistered)
        {
            _native.Unregister(_windowHandle, id);
        }
    }

    private int AllocateId()
    {
        if (_nextId > 0xBFFF)
        {
            throw new InvalidOperationException("No global hotkey identifiers remain.");
        }

        return _nextId++;
    }

    private static HotkeyRegistrationResult ConflictResult(bool previousBindingRetained) => new(
        false,
        "Windows could not register that hotkey because another application is using it.",
        previousBindingRetained);

    private sealed record Registration(HotkeyCommand Command, HotkeyBinding Binding, bool IsRegistered);
}
