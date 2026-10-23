using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace BridgEN.App.Input;

/// <summary>
/// Watches for Z and Enter key presses across the current Windows desktop.
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

    public GlobalKeyboardMonitor()
    {
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

                if (isDown && TryGetKeyName(keyboardData.VirtualKeyCode, out var keyName))
                {
                    if (_keysCurrentlyDown.Add(keyboardData.VirtualKeyCode))
                    {
                        KeyPressed?.Invoke(this, new TrackedKeyPressedEventArgs(keyName));
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

    [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    private static extern IntPtr GetModuleHandle(string? moduleName);
}

public sealed class TrackedKeyPressedEventArgs(string keyName) : EventArgs
{
    public string KeyName { get; } = keyName;
}