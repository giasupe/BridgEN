using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace BridgEN.App.Ocr;

/// <summary>A topmost, click-through caption shown over the selected game window.</summary>
public partial class OcrOverlayWindow : Window
{
    private const int ExtendedWindowStyle = -20;
    private const int TransparentStyle = 0x00000020;
    private const int ToolWindowStyle = 0x00000080;
    private const int NoActivateStyle = 0x08000000;
    private const uint NoSize = 0x0001;
    private const uint NoZOrder = 0x0004;
    private const uint NoActivate = 0x0010;

    private IntPtr _windowHandle;

    public OcrOverlayWindow()
    {
        InitializeComponent();
        SourceInitialized += OnSourceInitialized;
    }

    public void ShowRecognizedText(string text, IntPtr targetWindowHandle)
    {
        RecognizedTextBlock.Text = text;
        if (!IsVisible)
        {
            Show();
        }

        UpdateLayout();
        UpdatePosition(targetWindowHandle);
    }

    public void UpdatePosition(IntPtr targetWindowHandle)
    {
        if (_windowHandle == IntPtr.Zero || !IsVisible || !GetWindowRect(targetWindowHandle, out var bounds))
        {
            return;
        }

        var dpi = GetDpiForWindow(targetWindowHandle);
        var scale = (dpi == 0 ? 96d : dpi) / 96d;
        var overlayWidth = (int)Math.Ceiling(ActualWidth * scale);
        var left = bounds.Left + Math.Max(0, (bounds.Right - bounds.Left - overlayWidth) / 2);
        var top = bounds.Top + (int)Math.Ceiling(24 * scale);

        SetWindowPos(_windowHandle, IntPtr.Zero, left, top, 0, 0, NoSize | NoZOrder | NoActivate);
    }

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        _windowHandle = new WindowInteropHelper(this).Handle;
        var style = GetWindowLongPointer(_windowHandle, ExtendedWindowStyle).ToInt64();
        style |= TransparentStyle | ToolWindowStyle | NoActivateStyle;
        SetWindowLongPointer(_windowHandle, ExtendedWindowStyle, new IntPtr(style));
    }

    private static IntPtr GetWindowLongPointer(IntPtr windowHandle, int index)
    {
        return IntPtr.Size == 8
            ? GetWindowLongPtr64(windowHandle, index)
            : new IntPtr(GetWindowLong32(windowHandle, index));
    }

    private static IntPtr SetWindowLongPointer(IntPtr windowHandle, int index, IntPtr value)
    {
        return IntPtr.Size == 8
            ? SetWindowLongPtr64(windowHandle, index, value)
            : new IntPtr(SetWindowLong32(windowHandle, index, value.ToInt32()));
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WindowRectangle
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW", SetLastError = true)]
    private static extern IntPtr GetWindowLongPtr64(IntPtr windowHandle, int index);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongW", SetLastError = true)]
    private static extern int GetWindowLong32(IntPtr windowHandle, int index);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)]
    private static extern IntPtr SetWindowLongPtr64(IntPtr windowHandle, int index, IntPtr value);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongW", SetLastError = true)]
    private static extern int SetWindowLong32(IntPtr windowHandle, int index, int value);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(IntPtr windowHandle, out WindowRectangle rectangle);

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr windowHandle);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(
        IntPtr windowHandle,
        IntPtr insertAfter,
        int x,
        int y,
        int width,
        int height,
        uint flags);
}
