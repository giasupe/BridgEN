using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows.Input;

namespace BridgEN.App.Input;

/// <summary>
/// Watches for Z / Enter and a configurable manual OCR key across the desktop.
/// The hook passes every key through, so it does not consume game input.
/// </summary>
public sealed class GlobalKeyboardMonitor : IDisposable
{
    private const int LowLevelKeyboardHook = 13;
    private const int KeyDownMessage = 0x0100;
    private const int KeyUpMessage = 0x0101;
    private const int SystemKeyDownMessage = 0x0104;
    private const int SystemKeyUpMessage = 0x0105;
    private const uint VirtualKeyZ = 0x5A;
    private const uint VirtualKeyEnter = 0x0D;

    private readonly LowLevelKeyboardProc _hookCallback;
    private readonly HashSet<uint> _keysCurrentlyDown = [];
    private IntPtr _hookHandle;
    private ManualOcrHotkey? _manualHotkey;

    public GlobalKeyboardMonitor()
    {
        RefreshModifierState();
        _hookCallback = HandleKeyboardEvent;
        var moduleHandle = GetModuleHandle(null);
        _hookHandle = SetWindowsHookEx(LowLevelKeyboardHook, _hookCallback, moduleHandle, 0);

        if (_hookHandle == IntPtr.Zero)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }
    }

    /// <summary>Raised once when Z or Enter is pressed, not repeatedly while held.</summary>
    public event EventHandler<TrackedKeyPressedEventArgs>? KeyPressed;

    /// <summary>Subscribers must queue OCR work and return immediately to the hook.</summary>
    public event EventHandler? ManualOcrRequested;

    public void SetManualHotkey(ManualOcrHotkey? hotkey)
    {
        if (hotkey is { IsValid: false }) throw new ArgumentException("Invalid manual OCR key.", nameof(hotkey));
        _manualHotkey = hotkey;
        RefreshModifierState();
        // Configuring a key while it is held must not interpret its repeats as a new press.
        if (hotkey is not null && (GetAsyncKeyState(hotkey.VirtualKey) & 0x8000) != 0)
            _keysCurrentlyDown.Add((uint)hotkey.VirtualKey);
    }

    public void Dispose()
    {
        if (_hookHandle == IntPtr.Zero)
        {
            return;
        }

        UnhookWindowsHookEx(_hookHandle);
        _hookHandle = IntPtr.Zero;
        _keysCurrentlyDown.Clear();
    }

    private IntPtr HandleKeyboardEvent(int code, IntPtr messagePointer, IntPtr dataPointer)
    {
        if (code >= 0)
        {
            try
            {
                var message = messagePointer.ToInt32();
                var keyboardData = Marshal.PtrToStructure<KeyboardHookData>(dataPointer);
                var isDown = message is KeyDownMessage or SystemKeyDownMessage;
                var isUp = message is KeyUpMessage or SystemKeyUpMessage;

                if (isDown)
                {
                    if (_keysCurrentlyDown.Add(keyboardData.VirtualKeyCode))
                    {
                        if (TryGetKeyName(keyboardData.VirtualKeyCode, out var keyName))
                            KeyPressed?.Invoke(this, new TrackedKeyPressedEventArgs(keyName));

                        if (_manualHotkey is { } hotkey
                            && keyboardData.VirtualKeyCode == hotkey.VirtualKey
                            && CurrentModifiers() == hotkey.Modifiers)
                            ManualOcrRequested?.Invoke(this, EventArgs.Empty);
                    }
                }
                else if (isUp)
                {
                    _keysCurrentlyDown.Remove(keyboardData.VirtualKeyCode);
                }
            }
            catch (Exception exception)
            {
                // Managed exceptions must not escape through the native Windows callback.
                Debug.WriteLine(exception);
            }
        }

        return CallNextHookEx(_hookHandle, code, messagePointer, dataPointer);
    }

    private ModifierKeys CurrentModifiers()
    {
        var modifiers = ModifierKeys.None;
        if (_keysCurrentlyDown.Contains(0x11) || _keysCurrentlyDown.Contains(0xA2) || _keysCurrentlyDown.Contains(0xA3))
            modifiers |= ModifierKeys.Control;
        if (_keysCurrentlyDown.Contains(0x10) || _keysCurrentlyDown.Contains(0xA0) || _keysCurrentlyDown.Contains(0xA1))
            modifiers |= ModifierKeys.Shift;
        if (_keysCurrentlyDown.Contains(0x12) || _keysCurrentlyDown.Contains(0xA4) || _keysCurrentlyDown.Contains(0xA5))
            modifiers |= ModifierKeys.Alt;
        if (_keysCurrentlyDown.Contains(0x5B) || _keysCurrentlyDown.Contains(0x5C)) modifiers |= ModifierKeys.Windows;
        return modifiers;
    }

    private void RefreshModifierState()
    {
        // Read only outside the hook. The current hook event has not updated
        // GetAsyncKeyState yet; inside the callback we track down/up events instead.
        foreach (uint key in new uint[] { 0xA0, 0xA1, 0xA2, 0xA3, 0xA4, 0xA5, 0x5B, 0x5C })
        {
            if ((GetAsyncKeyState((int)key) & 0x8000) != 0) _keysCurrentlyDown.Add(key);
            else _keysCurrentlyDown.Remove(key);
        }
    }

    private static bool TryGetKeyName(uint virtualKey, out string keyName)
    {
        switch (virtualKey)
        {
            case VirtualKeyZ:
                keyName = "Z";
                return true;
            case VirtualKeyEnter:
                keyName = "Enter";
                return true;
            default:
                keyName = string.Empty;
                return false;
        }
    }

    private delegate IntPtr LowLevelKeyboardProc(int code, IntPtr message, IntPtr data);

    [StructLayout(LayoutKind.Sequential)]
    private struct KeyboardHookData
    {
        public uint VirtualKeyCode;
        public uint ScanCode;
        public uint Flags;
        public uint Time;
        public IntPtr ExtraInfo;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(
        int hookType,
        LowLevelKeyboardProc callback,
        IntPtr moduleHandle,
        uint threadId);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnhookWindowsHookEx(IntPtr hookHandle);

    [DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(
        IntPtr hookHandle,
        int code,
        IntPtr message,
        IntPtr data);

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int virtualKey);

    [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    private static extern IntPtr GetModuleHandle(string? moduleName);
}

public sealed class TrackedKeyPressedEventArgs(string keyName) : EventArgs
{
    public string KeyName { get; } = keyName;
}
