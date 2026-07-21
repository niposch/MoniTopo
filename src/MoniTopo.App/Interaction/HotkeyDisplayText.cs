using MoniTopo.Core.Models;

namespace MoniTopo.App.Interaction;

public static class HotkeyDisplayText
{
    public static string Format(HotkeyBinding? binding)
    {
        if (binding is null)
        {
            return string.Empty;
        }

        var parts = new List<string>();
        if (binding.Modifiers.HasFlag(HotkeyModifiers.Control)) parts.Add("Ctrl");
        if (binding.Modifiers.HasFlag(HotkeyModifiers.Alt)) parts.Add("Alt");
        if (binding.Modifiers.HasFlag(HotkeyModifiers.Shift)) parts.Add("Shift");
        if (binding.Modifiers.HasFlag(HotkeyModifiers.Windows)) parts.Add("Win");
        parts.Add(binding.VirtualKey is >= 0x30 and <= 0x5A
            ? ((char)binding.VirtualKey).ToString()
            : $"VK {binding.VirtualKey:X2}");
        return string.Join('+', parts);
    }
}
