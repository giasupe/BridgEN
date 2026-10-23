using System.Windows.Input;
using System.Text.Json.Serialization;

namespace BridgEN.App.Input;

/// <summary>A keyboard key with optional modifiers, shared by settings and the hook.</summary>
public sealed record ManualOcrHotkey(int VirtualKey, ModifierKeys Modifiers)
{
    public static ManualOcrHotkey Default { get; } = new(0x77, ModifierKeys.None); // F8

    [JsonIgnore]
    public bool IsValid => VirtualKey is >= 0x08 and <= 0xFE
        && VirtualKey is not (0x10 or 0x11 or 0x12 or 0x5B or 0x5C
            or 0xA0 or 0xA1 or 0xA2 or 0xA3 or 0xA4 or 0xA5)
        && KeyInterop.KeyFromVirtualKey(VirtualKey) != Key.None
        && (Modifiers & ~(ModifierKeys.Control | ModifierKeys.Shift | ModifierKeys.Alt | ModifierKeys.Windows)) == 0;

    [JsonIgnore]
    public string DisplayName
    {
        get
        {
            var parts = new List<string>();
            if (Modifiers.HasFlag(ModifierKeys.Control)) parts.Add("Ctrl");
            if (Modifiers.HasFlag(ModifierKeys.Shift)) parts.Add("Shift");
            if (Modifiers.HasFlag(ModifierKeys.Alt)) parts.Add("Alt");
            if (Modifiers.HasFlag(ModifierKeys.Windows)) parts.Add("Win");
            parts.Add(KeyInterop.KeyFromVirtualKey(VirtualKey) switch
            {
                Key.Return => "Enter",
                Key.Escape => "Esc",
                var key => key.ToString()
            });
            return string.Join(" + ", parts);
        }
    }
}
