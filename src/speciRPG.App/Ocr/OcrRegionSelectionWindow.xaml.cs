using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;

namespace speciRPG.App.Ocr;

/// <summary>Lets the user drag a rectangle over the target window without activating this window.</summary>
public partial class OcrRegionSelectionWindow : Window
{
    private const int ExtendedWindowStyle = -20;
    private const int ToolWindowStyle = 0x00000080;
    private const int NoActivateStyle = 0x08000000;
    private const int NoActivate = 0x0010;
    private const int ShowWindow = 0x0040;
    private static readonly IntPtr TopmostWindow = new(-1);

    private IntPtr _windowHandle;
    private Point _dragStart;
    private bool _isDragging;

    public OcrRegionSelectionWindow()
    {
        InitializeComponent();
        SourceInitialized += OnSourceInitialized;
    }

    public event EventHandler<OcrRegionSelectedEventArgs>? RegionSelected;
    public event EventHandler? SelectionCancelled;

    public bool ShowOverTarget(IntPtr targetWindowHandle)
    {
        if (targetWindowHandle == IntPtr.Zero
            || IsIconic(targetWindowHandle)
            || !GetWindowRect(targetWindowHandle, out var bounds)
            || bounds.Right <= bounds.Left
            || bounds.Bottom <= bounds.Top)
        {
            return false;
        }

        _isDragging = false;
        SelectionRectangle.Visibility = Visibility.Collapsed;

        if (!IsVisible)
        {
            Show();
        }

        UpdateLayout();
        var moved = SetWindowPos(
            _windowHandle,
            TopmostWindow,
            bounds.Left,
            bounds.Top,
            bounds.Right - bounds.Left,
            bounds.Bottom - bounds.Top,
            NoActivate | ShowWindow);
        if (!moved)
        {
            Hide();
        }

        return moved;
    }

    private void SelectionCanvas_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _dragStart = e.GetPosition(SelectionCanvas);
        _isDragging = true;
        SelectionRectangle.Visibility = Visibility.Visible;
        UpdateSelectionRectangle(_dragStart);
        SelectionCanvas.CaptureMouse();
        e.Handled = true;
    }

    private void SelectionCanvas_MouseMove(object sender, MouseEventArgs e)
    {
        if (_isDragging)
        {
            UpdateSelectionRectangle(e.GetPosition(SelectionCanvas));
        }
    }

    private void SelectionCanvas_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (!_isDragging)
        {
            return;
        }

        var end = e.GetPosition(SelectionCanvas);
        UpdateSelectionRectangle(end);
        _isDragging = false;
        SelectionCanvas.ReleaseMouseCapture();

        var left = Math.Min(_dragStart.X, end.X);
        var top = Math.Min(_dragStart.Y, end.Y);
        var width = Math.Abs(end.X - _dragStart.X);
        var height = Math.Abs(end.Y - _dragStart.Y);
        if (SelectionCanvas.ActualWidth <= 0
            || SelectionCanvas.ActualHeight <= 0
            || width < 12
            || height < 12)
        {
            SelectionRectangle.Visibility = Visibility.Collapsed;
            return;
        }

        var normalizedLeft = Math.Clamp(left / SelectionCanvas.ActualWidth, 0, 1);
        var normalizedTop = Math.Clamp(top / SelectionCanvas.ActualHeight, 0, 1);
        var region = new OcrCaptureRegion(
            normalizedLeft,
            normalizedTop,
            Math.Clamp(width / SelectionCanvas.ActualWidth, 0, 1 - normalizedLeft),
            Math.Clamp(height / SelectionCanvas.ActualHeight, 0, 1 - normalizedTop));

        Hide();
        RegionSelected?.Invoke(this, new OcrRegionSelectedEventArgs(region));
        e.Handled = true;
    }

    private void SelectionCanvas_MouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        _isDragging = false;
        SelectionCanvas.ReleaseMouseCapture();
        SelectionRectangle.Visibility = Visibility.Collapsed;
        Hide();
        SelectionCancelled?.Invoke(this, EventArgs.Empty);
        e.Handled = true;
    }

    private void UpdateSelectionRectangle(Point end)
    {
        var left = Math.Min(_dragStart.X, end.X);
        var top = Math.Min(_dragStart.Y, end.Y);
        Canvas.SetLeft(SelectionRectangle, left);
        Canvas.SetTop(SelectionRectangle, top);
        SelectionRectangle.Width = Math.Abs(end.X - _dragStart.X);
        SelectionRectangle.Height = Math.Abs(end.Y - _dragStart.Y);
    }

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        _windowHandle = new WindowInteropHelper(this).Handle;
        var style = GetWindowLongPointer(_windowHandle, ExtendedWindowStyle).ToInt64();
        style |= ToolWindowStyle | NoActivateStyle;
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
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsIconic(IntPtr windowHandle);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(
        IntPtr windowHandle,
        IntPtr insertAfter,
        int x,
        int y,
        int width,
        int height,
        int flags);
}
