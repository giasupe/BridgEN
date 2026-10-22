using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace speciRPG.App.Input;

/// <summary>A visible application window that can be selected as the input target.</summary>
public sealed record WindowTarget(IntPtr Handle, string Title, string ProcessName)
{
    public string DisplayName => $"{Title}  ({ProcessName})";
}

/// <summary>Lists selectable windows and checks which window is currently in front.</summary>
public static class WindowTargetProvider
{
    public static IReadOnlyList<WindowTarget> GetOpenWindows()
    {
        var windows = new List<WindowTarget>();
        var ownProcessId = Environment.ProcessId;

        EnumWindows((windowHandle, _) =>
        {
            if (!IsWindowVisible(windowHandle))
            {
                return true;
            }

            var titleLength = GetWindowTextLength(windowHandle);
            if (titleLength == 0)
            {
                return true;
            }

            var titleBuilder = new StringBuilder(titleLength + 1);
            GetWindowText(windowHandle, titleBuilder, titleBuilder.Capacity);
            var title = titleBuilder.ToString().Trim();

            if (string.IsNullOrWhiteSpace(title))
            {
                return true;
            }

            GetWindowThreadProcessId(windowHandle, out var processId);
            if (processId == 0 || processId == ownProcessId)
            {
                return true;
            }

            try
            {
                using var process = Process.GetProcessById((int)processId);
                windows.Add(new WindowTarget(windowHandle, title, process.ProcessName));
            }
            catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or Win32Exception)
            {
                // The window can close while the list is being collected.
            }

            return true;
        }, IntPtr.Zero);

        return windows
            .OrderBy(window => window.Title, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(window => window.ProcessName, StringComparer.CurrentCultureIgnoreCase)
            .ToArray();
    }

    public static bool IsActive(WindowTarget? target)
    {
        return target is not null
            && target.Handle != IntPtr.Zero
            && GetForegroundWindow() == target.Handle;
    }

    private delegate bool EnumWindowsProc(IntPtr windowHandle, IntPtr parameter);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr parameter);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(IntPtr windowHandle);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowTextLength(IntPtr windowHandle);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int GetWindowText(IntPtr windowHandle, StringBuilder text, int maxCount);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint GetWindowThreadProcessId(IntPtr windowHandle, out uint processId);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();
}
