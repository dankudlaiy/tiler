using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Threading;
using Microsoft.Win32;
using Tiler.Core;
using static Tiler.NativeMethods;

namespace Tiler;

/// <summary>
/// Keeps every ordinary window tiled, one <see cref="Workspace"/> per monitor, and re-tiles
/// when windows are dragged, opened, closed, minimized, maximized or restored.
/// Everything runs on the UI thread: out-of-context WinEvents are delivered through its message loop.
/// </summary>
internal sealed class TilerController : IDisposable
{
    /// <summary>A press on the title bar that moves the mouse less than this is a click, not a drag.</summary>
    const int DragThreshold = 8;

    readonly Settings settings;
    readonly Animator animator = new();
    readonly OverlayWindow overlay = new();
    readonly DispatcherTimer dragTimer;
    readonly WinEventDelegate winEventProc; // kept in a field so the GC doesn't collect the native callback
    readonly List<nint> hooks = [];
    readonly Dictionary<nint, Monitor> monitors = [];
    readonly HashSet<nint> floating = [];
    readonly Dictionary<nint, IntSize> minSizes = [];
    readonly HashSet<Monitor> dirty = [];
    bool arrangeScheduled;
    bool paused;
    DragSession? drag;

    public TilerController(Settings settings)
    {
        this.settings = settings;
        animator.DurationMs = settings.AnimationMs;
        animator.SizeRefused += OnSizeRefused;
        dragTimer = new DispatcherTimer(DispatcherPriority.Input) { Interval = TimeSpan.FromMilliseconds(15) };
        dragTimer.Tick += (_, _) => UpdateDrag();

        winEventProc = OnWinEvent;
        Hook(EVENT_SYSTEM_MOVESIZESTART, EVENT_SYSTEM_MOVESIZEEND);
        Hook(EVENT_SYSTEM_MINIMIZESTART, EVENT_SYSTEM_MINIMIZEEND);
        Hook(EVENT_OBJECT_DESTROY, EVENT_OBJECT_HIDE);
        Hook(EVENT_OBJECT_LOCATIONCHANGE, EVENT_OBJECT_LOCATIONCHANGE);
        Hook(EVENT_OBJECT_CLOAKED, EVENT_OBJECT_UNCLOAKED);
        SystemEvents.DisplaySettingsChanged += OnDisplaySettingsChanged;

        Retile();
    }

    public bool Paused
    {
        get => paused;
        set
        {
            if (paused == value)
                return;
            paused = value;
            Log.Info(paused ? "Paused" : "Resumed");
            if (paused)
            {
                drag = null;
                dragTimer.Stop();
                overlay.HidePreview();
            }
            else
            {
                Retile();
            }
        }
    }

    /// <summary>Forgets the layout and tiles all windows again, each near where it is now.</summary>
    public void Retile()
    {
        monitors.Clear();
        dirty.Clear();
        minSizes.Clear();
        var windows = new List<nint>();
        EnumWindows((hwnd, _) =>
        {
            if (!floating.Contains(hwnd) && WindowOps.IsTileable(hwnd))
                windows.Add(hwnd);
            return true;
        }, 0);
        // Bottom of the z-order first, so the windows in front are placed last and keep their spot best.
        windows.Reverse();
        foreach (var hwnd in windows)
            Tile(hwnd, null);
        Log.Info($"Tiled {windows.Count} windows on {monitors.Count} monitor(s)");
    }

    public void SetGap(int gap)
    {
        settings.Gap = gap;
        foreach (var monitor in monitors.Values)
        {
            monitor.Layout.Gap = gap;
            monitor.Layout.Bounds = LayoutBounds(monitor.WorkArea);
            MarkDirty(monitor);
        }
    }

    public void SetAnimation(int durationMs)
    {
        settings.AnimationMs = durationMs;
        animator.DurationMs = durationMs;
    }

    public void Dispose()
    {
        SystemEvents.DisplaySettingsChanged -= OnDisplaySettingsChanged;
        foreach (var hook in hooks)
            UnhookWinEvent(hook);
        hooks.Clear();
        dragTimer.Stop();
        overlay.Close();
    }

    void Hook(uint min, uint max)
    {
        var hook = SetWinEventHook(min, max, 0, winEventProc, 0, 0, WINEVENT_OUTOFCONTEXT | WINEVENT_SKIPOWNPROCESS);
        if (hook == 0)
            Log.Error($"SetWinEventHook 0x{min:X} failed");
        else
            hooks.Add(hook);
    }

    void OnWinEvent(nint hook, uint type, nint hwnd, int idObject, int idChild, uint thread, uint time)
    {
        if (paused || hwnd == 0 || idObject != OBJID_WINDOW || idChild != CHILDID_SELF)
            return;
        // An exception escaping into native code would take the whole process down.
        try
        {
            switch (type)
            {
                case EVENT_SYSTEM_MOVESIZESTART:
                    BeginDrag(hwnd);
                    break;
                case EVENT_SYSTEM_MOVESIZEEND:
                    EndDrag(hwnd);
                    break;
                case EVENT_OBJECT_DESTROY:
                    floating.Remove(hwnd);
                    minSizes.Remove(hwnd);
                    Untile(hwnd);
                    break;
                case EVENT_SYSTEM_MINIMIZESTART:
                case EVENT_OBJECT_HIDE:
                case EVENT_OBJECT_CLOAKED:
                    Untile(hwnd);
                    break;
                case EVENT_SYSTEM_MINIMIZEEND:
                case EVENT_OBJECT_SHOW:
                case EVENT_OBJECT_UNCLOAKED:
                case EVENT_OBJECT_LOCATIONCHANGE:
                    Reconsider(hwnd);
                    break;
            }
        }
        catch (Exception e)
        {
            Log.Error($"WinEvent 0x{type:X} for 0x{hwnd:X}", e);
        }
    }

    void OnDisplaySettingsChanged(object? sender, EventArgs e) =>
        overlay.Dispatcher.BeginInvoke(DispatcherPriority.Background, () =>
        {
            Log.Info("Display settings changed");
            if (!paused)
                Retile();
        });

    /// <summary>
    /// A window changed state on its own: joins the layout when it became an ordinary window
    /// (restored, shown, new) and leaves it when it stopped being one (maximized, went fullscreen).
    /// </summary>
    void Reconsider(nint hwnd)
    {
        if (drag?.Window == hwnd || GetAncestor(hwnd, GA_ROOT) != hwnd)
            return;
        if (Owner(hwnd) != null)
        {
            if (!WindowOps.IsTileable(hwnd))
                Untile(hwnd);
        }
        else if (!floating.Contains(hwnd) && WindowOps.IsTileable(hwnd))
        {
            Log.Info($"Tiling {WindowOps.Describe(hwnd)}");
            Tile(hwnd, null);
        }
    }

    void BeginDrag(nint hwnd)
    {
        // Maximized windows count: dragging one restores it, and it joins the layout where it's dropped.
        if (!WindowOps.IsTileable(hwnd, allowMaximized: true))
            return;

        animator.Cancel(hwnd);
        drag = new DragSession(hwnd, WindowOps.CursorPos(), WindowOps.VisualRect(hwnd), IsZoomed(hwnd));
        dragTimer.Start();
    }

    /// <summary>
    /// Moving keeps the size; resizing keeps at least one edge in place. (Asking the window what's under
    /// the cursor doesn't work: by the time the start event arrives, the cursor has already moved on.)
    /// </summary>
    static bool IsResize(IntRect start, IntRect now) =>
        (now.Width != start.Width || now.Height != start.Height)
        && (now.Left == start.Left || now.Top == start.Top || now.Right == start.Right || now.Bottom == start.Bottom);

    void DetectResize(DragSession d)
    {
        // A maximized window can't be resized; dragging it only restores it, which changes its size.
        if (d.Resizing || d.StartedMaximized || d.StartRect is not { } start || WindowOps.VisualRect(d.Window) is not { } now)
            return;
        d.Resizing = IsResize(start, now);
    }

    void UpdateDrag()
    {
        if (drag is not { } d)
        {
            dragTimer.Stop();
            return;
        }

        DetectResize(d);
        if (d.Resizing)
        {
            overlay.HidePreview();
            return;
        }

        var cursor = WindowOps.CursorPos();
        if (!d.Moved)
        {
            if (Math.Abs(cursor.X - d.StartCursor.X) < DragThreshold && Math.Abs(cursor.Y - d.StartCursor.Y) < DragThreshold)
                return;
            d.Moved = true;
        }
        d.LastCursor = cursor;
        if (WindowOps.IsKeyDown(VK_ESCAPE))
            d.Cancelled = true;
        d.Floating = WindowOps.IsKeyDown(settings.FloatVirtualKey);

        if (d.Cancelled || d.Floating)
        {
            overlay.HidePreview();
            return;
        }

        var monitor = MonitorAt(cursor);
        var preview = monitor.Layout.Clone();
        Drop(preview, d.Window, cursor);
        overlay.ShowPreview(monitor.WorkArea, preview.Arrange(), d.Window);
    }

    void EndDrag(nint hwnd)
    {
        if (drag is not { } d || d.Window != hwnd)
            return;
        // No final UpdateDrag: this event arrives after the mouse button is up, and by then the modifier
        // is often released too. The drop must match what the preview last showed.
        drag = null;
        dragTimer.Stop();
        overlay.HidePreview();
        DetectResize(d);

        var owner = Owner(hwnd);
        if (d.Resizing)
        {
            if (owner != null && WindowOps.VisualRect(hwnd) is { } rect)
            {
                owner.Layout.Resize(hwnd, rect);
                MarkDirty(owner);
            }
            return;
        }

        // Esc makes Windows put the window back exactly where it was; a quick tap can slip between two
        // polls of the key, so an unchanged rect counts as a cancel too.
        if (d.Cancelled || !d.Moved || WindowOps.VisualRect(hwnd) == d.StartRect)
        {
            // Back into its tile.
            if (owner != null)
                MarkDirty(owner);
            return;
        }

        if (d.Floating || IsZoomed(hwnd))
        {
            // Floated on purpose, or Windows maximized it because it was dropped on the top edge.
            if (d.Floating)
            {
                floating.Add(hwnd);
                Log.Info($"Floating {WindowOps.Describe(hwnd)}");
            }
            Untile(hwnd);
            return;
        }

        floating.Remove(hwnd);
        var target = MonitorAt(d.LastCursor);
        if (owner != null && owner != target)
        {
            owner.Layout.Remove(hwnd);
            MarkDirty(owner);
        }
        Drop(target.Layout, hwnd, d.LastCursor);
        MarkDirty(target);
        Log.Info($"Dropped {WindowOps.Describe(hwnd)} at {d.LastCursor.X},{d.LastCursor.Y}");
    }

    static void Drop(Workspace layout, nint hwnd, IntPoint point)
    {
        if (layout.Contains(hwnd))
            layout.MoveTo(hwnd, point);
        else
            layout.Add(hwnd, point, keepTarget: true);
    }

    void Tile(nint hwnd, IntPoint? point)
    {
        var at = point ?? (WindowOps.VisualRect(hwnd) is { } rect ? WindowOps.Center(rect) : WindowOps.CursorPos());
        var monitor = MonitorAt(at);
        monitor.Layout.Add(hwnd, at);
        MarkDirty(monitor);
    }

    void Untile(nint hwnd)
    {
        if (Owner(hwnd) is not { } monitor)
            return;
        monitor.Layout.Remove(hwnd);
        MarkDirty(monitor);
        Log.Info($"Untiled 0x{hwnd:X}");
    }

    Monitor? Owner(nint hwnd) => monitors.Values.FirstOrDefault(m => m.Layout.Contains(hwnd));

    Monitor MonitorAt(IntPoint point)
    {
        var handle = WindowOps.MonitorAt(point);
        if (!monitors.TryGetValue(handle, out var monitor))
        {
            var workArea = WindowOps.WorkArea(handle);
            monitor = new Monitor(workArea, new Workspace(LayoutBounds(workArea), settings.Gap) { MinSize = MinSizeOf });
            monitors[handle] = monitor;
        }
        return monitor;
    }

    IntSize MinSizeOf(nint hwnd)
    {
        if (!minSizes.TryGetValue(hwnd, out var size))
        {
            // A window that doesn't answer gets no minimum rather than being asked again on every layout pass.
            size = WindowOps.MinVisualSize(hwnd) ?? default;
            minSizes[hwnd] = size;
        }
        return size;
    }

    /// <summary>
    /// Some apps enforce a minimum size without reporting it. When a window ends up bigger than its
    /// tile, remember that size as its minimum and lay out again.
    /// </summary>
    void OnSizeRefused(nint hwnd, IntSize tile, IntSize actual)
    {
        var known = MinSizeOf(hwnd);
        var learned = new IntSize(
            actual.Width > tile.Width ? Math.Max(known.Width, actual.Width) : known.Width,
            actual.Height > tile.Height ? Math.Max(known.Height, actual.Height) : known.Height);
        if (learned == known || Owner(hwnd) is not { } owner)
            return;
        minSizes[hwnd] = learned;
        Log.Info($"Learned minimum {learned.Width}x{learned.Height} of {WindowOps.Describe(hwnd)}");

        // It was placed before its real minimum was known; place it again now that it is.
        if (!owner.Layout.Fits() && owner.Layout.Arrange().TryGetValue(hwnd, out var rect))
        {
            owner.Layout.Remove(hwnd);
            owner.Layout.Add(hwnd, WindowOps.Center(rect));
        }
        MarkDirty(owner);
    }

    /// <summary>The same gap around the screen edge as between tiles.</summary>
    IntRect LayoutBounds(IntRect workArea) =>
        new(workArea.Left + settings.Gap, workArea.Top + settings.Gap, workArea.Right - settings.Gap, workArea.Bottom - settings.Gap);

    /// <summary>Batches layout changes: several events in a row produce a single animation.</summary>
    void MarkDirty(Monitor monitor)
    {
        dirty.Add(monitor);
        if (arrangeScheduled)
            return;
        arrangeScheduled = true;
        overlay.Dispatcher.BeginInvoke(DispatcherPriority.Background, ArrangeDirty);
    }

    void ArrangeDirty()
    {
        arrangeScheduled = false;
        var targets = new Dictionary<nint, IntRect>();
        foreach (var monitor in dirty)
        {
            foreach (var (hwnd, rect) in monitor.Layout.Arrange())
            {
                // The window in the user's hand goes to its tile when it's dropped.
                if (hwnd != drag?.Window)
                    targets[hwnd] = rect;
            }
        }
        dirty.Clear();
        animator.Animate(targets);
    }

    sealed class Monitor(IntRect workArea, Workspace layout)
    {
        public IntRect WorkArea { get; } = workArea;
        public Workspace Layout { get; } = layout;
    }

    sealed class DragSession(nint window, IntPoint startCursor, IntRect? startRect, bool startedMaximized)
    {
        public nint Window { get; } = window;
        public IntPoint StartCursor { get; } = startCursor;
        public IntRect? StartRect { get; } = startRect;
        public bool StartedMaximized { get; } = startedMaximized;
        public bool Resizing { get; set; }
        public bool Moved { get; set; }
        public IntPoint LastCursor { get; set; } = startCursor;
        public bool Cancelled { get; set; }
        public bool Floating { get; set; }
    }
}
