using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using Tiler.Core;
using static Tiler.NativeMethods;

namespace Tiler;

internal static class WindowOps
{
    // Shell surfaces that look like ordinary resizable windows but must never be tiled.
    static readonly HashSet<string> IgnoredClasses = new(StringComparer.Ordinal)
    {
        "Progman",
        "WorkerW",
        "Shell_TrayWnd",
        "Shell_SecondaryTrayWnd",
        "Windows.UI.Core.CoreWindow",
        "XamlExplorerHostIslandWindow",
        "TopLevelWindowForOverflowXamlIsland",
        "NotifyIconOverflowWindow",
        "MultitaskingViewFrame",
        "ForegroundStaging",
    };

    public static readonly uint OwnProcessId = (uint)Environment.ProcessId;

    /// <summary>Development aid: when set, only windows whose title starts with it are tiled.</summary>
    public static string? TitleFilter { get; set; }

    /// <summary>The frame the user sees, without the invisible resize borders of Windows 10/11.</summary>
    public static IntRect? VisualRect(nint hwnd)
    {
        if (DwmGetWindowAttribute(hwnd, DWMWA_EXTENDED_FRAME_BOUNDS, out RECT r, Marshal.SizeOf<RECT>()) == 0)
            return ToRect(r);
        return WindowRect(hwnd);
    }

    public static IntRect? WindowRect(nint hwnd) => GetWindowRect(hwnd, out RECT r) ? ToRect(r) : null;

    /// <summary>Cloaked windows are "visible" but not shown: other virtual desktops, suspended UWP apps.</summary>
    public static bool IsCloaked(nint hwnd) =>
        DwmGetWindowAttribute(hwnd, DWMWA_CLOAKED, out int cloaked, sizeof(int)) == 0 && cloaked != 0;

    public static uint ProcessId(nint hwnd)
    {
        GetWindowThreadProcessId(hwnd, out uint pid);
        return pid;
    }

    public static string ClassName(nint hwnd)
    {
        var sb = new StringBuilder(256);
        GetClassNameW(hwnd, sb, sb.Capacity);
        return sb.ToString();
    }

    public static string Title(nint hwnd)
    {
        var sb = new StringBuilder(256);
        GetWindowTextW(hwnd, sb, sb.Capacity);
        return sb.ToString();
    }

    public static string Describe(nint hwnd) => $"0x{hwnd:X} [{ClassName(hwnd)}] \"{Title(hwnd)}\"";

    /// <summary>
    /// Ordinary resizable top-level windows only: no maximized or fullscreen windows,
    /// dialogs, tool windows, always-on-top windows, popups or shell surfaces.
    /// </summary>
    /// <param name="allowMaximized">Accept a maximized window: dragging it restores it.</param>
    public static bool IsTileable(nint hwnd, bool allowMaximized = false)
    {
        if (hwnd == 0 || !IsWindow(hwnd) || !IsWindowVisible(hwnd) || IsIconic(hwnd))
            return false;
        if (!allowMaximized && IsZoomed(hwnd))
            return false;
        if (GetAncestor(hwnd, GA_ROOT) != hwnd || ProcessId(hwnd) == OwnProcessId)
            return false;

        long style = GetWindowLong(hwnd, GWL_STYLE);
        long exStyle = GetWindowLong(hwnd, GWL_EXSTYLE);
        if ((style & WS_CHILD) != 0 || (style & WS_THICKFRAME) == 0)
            return false;
        // Always-on-top windows are picture-in-picture players, widgets and the like: they float.
        if ((exStyle & (WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE | WS_EX_TOPMOST)) != 0)
            return false;
        // Owned windows are usually dialogs; the ones that can maximize are real app windows.
        if (GetWindow(hwnd, GW_OWNER) != 0 && (style & WS_MAXIMIZEBOX) == 0)
            return false;
        if (IsCloaked(hwnd) || IgnoredClasses.Contains(ClassName(hwnd)))
            return false;
        if (TitleFilter != null && !Title(hwnd).StartsWith(TitleFilter, StringComparison.Ordinal))
            return false;

        return VisualRect(hwnd) is { IsEmpty: false } rect && !CoversMonitor(hwnd, rect);
    }

    static bool CoversMonitor(nint hwnd, IntRect rect)
    {
        if (MonitorInfo(MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST)) is not { } info)
            return false;
        var monitor = ToRect(info.rcMonitor);
        return rect.Left <= monitor.Left && rect.Top <= monitor.Top && rect.Right >= monitor.Right && rect.Bottom >= monitor.Bottom;
    }

    public static nint MonitorAt(IntPoint p) => MonitorFromPoint(new POINT { X = p.X, Y = p.Y }, MONITOR_DEFAULTTONEAREST);

    /// <summary>The monitor without the taskbar and docked app bars.</summary>
    public static IntRect WorkArea(nint monitor) => MonitorInfo(monitor) is { } info ? ToRect(info.rcWork) : default;

    static MONITORINFO? MonitorInfo(nint monitor)
    {
        var info = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
        return GetMonitorInfoW(monitor, ref info) ? info : null;
    }

    public static IntPoint Center(IntRect r) => new(r.Left + r.Width / 2, r.Top + r.Height / 2);

    /// <summary>
    /// The smallest visible size the window accepts, from WM_GETMINMAXINFO. Null when the window doesn't answer in time.
    /// </summary>
    public static IntSize? MinVisualSize(nint hwnd)
    {
        // Pre-filled with the system defaults, as Windows does itself: apps usually adjust only the fields they care about.
        var info = new MINMAXINFO
        {
            ptMinTrackSize = new POINT { X = GetSystemMetrics(SM_CXMINTRACK), Y = GetSystemMetrics(SM_CYMINTRACK) },
            ptMaxTrackSize = new POINT { X = GetSystemMetrics(SM_CXMAXTRACK), Y = GetSystemMetrics(SM_CYMAXTRACK) },
        };
        // SMTO_BLOCK: don't dispatch other messages (and so our own WinEvent callbacks) while waiting.
        if (SendMessageTimeoutW(hwnd, WM_GETMINMAXINFO, 0, ref info, SMTO_BLOCK | SMTO_ABORTIFHUNG, 100, out _) == 0)
            return null;
        if (FrameOf(hwnd) is not { } frame)
            return null;

        // A DPI-unaware app answers in its own 96-DPI pixels; Windows only scales it when it sends the message itself.
        double scale = 1;
        uint windowDpi = GetDpiForWindow(hwnd);
        if (windowDpi > 0 && GetDpiForMonitor(MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST), MDT_EFFECTIVE_DPI, out uint monitorDpi, out _) == 0)
            scale = monitorDpi / (double)windowDpi;

        // The track size is the whole window rect; the tile is the visible frame.
        int width = (int)Math.Ceiling(info.ptMinTrackSize.X * scale) - frame.Left - frame.Right;
        int height = (int)Math.Ceiling(info.ptMinTrackSize.Y * scale) - frame.Top - frame.Bottom;
        return new IntSize(Math.Max(0, width), Math.Max(0, height));
    }

    /// <summary>How far the window rect extends past the visible frame on each side (the invisible resize borders).</summary>
    public static Frame? FrameOf(nint hwnd)
    {
        if (WindowRect(hwnd) is not { } outer || VisualRect(hwnd) is not { } inner)
            return null;
        return new Frame(inner.Left - outer.Left, inner.Top - outer.Top, outer.Right - inner.Right, outer.Bottom - inner.Bottom);
    }

    /// <summary>Places the window so that its visible frame matches <paramref name="visual"/>.</summary>
    /// <param name="frame">Borders measured beforehand; animations pass them to skip measuring on every frame.</param>
    /// <param name="async">Don't wait for the window to process the move, so a hung app can't block the caller.</param>
    public static void MoveVisual(nint hwnd, IntRect visual, Frame? frame = null, bool async = true)
    {
        if ((frame ?? FrameOf(hwnd)) is not { } f)
            return;
        uint flags = SWP_NOZORDER | SWP_NOACTIVATE | SWP_NOOWNERZORDER | (async ? SWP_ASYNCWINDOWPOS : 0);
        SetWindowPos(hwnd, 0, visual.Left - f.Left, visual.Top - f.Top, visual.Width + f.Left + f.Right, visual.Height + f.Top + f.Bottom, flags);
    }

    public static bool IsKeyDown(int virtualKey) => (GetAsyncKeyState(virtualKey) & 0x8000) != 0;

    public static IntPoint CursorPos() => GetCursorPos(out POINT p) ? new IntPoint(p.X, p.Y) : default;

    static IntRect ToRect(RECT r) => new(r.Left, r.Top, r.Right, r.Bottom);
}

internal readonly record struct Frame(int Left, int Top, int Right, int Bottom);
