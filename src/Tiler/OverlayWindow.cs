using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using Microsoft.Win32;
using Tiler.Core;
using static Tiler.NativeMethods;

namespace Tiler;

/// <summary>
/// Click-through topmost window over a monitor's work area that previews the layout a drop
/// would produce: one "ghost" per tile, the dragged window's highlighted. Ghosts start at the
/// windows' current positions and glide to their tiles, and morph whenever the preview changes.
/// Canvas units are physical pixels.
/// </summary>
internal sealed class OverlayWindow : Window
{
    static readonly Color FallbackAccent = Color.FromRgb(0x3B, 0x82, 0xF6);
    static readonly Duration MorphDuration = TimeSpan.FromMilliseconds(180);
    static readonly Duration FadeDuration = TimeSpan.FromMilliseconds(120);
    static readonly IEasingFunction Ease = Freeze(new CubicEase { EasingMode = EasingMode.EaseOut });

    readonly Canvas canvas = new() { SnapsToDevicePixels = true, Opacity = 0 };
    readonly Rectangle dim = new() { Fill = Brush(0x59, Colors.Black) };
    readonly Dictionary<nint, Border> ghosts = [];
    readonly Color accent = ReadAccentColor();
    readonly nint hwnd;

    IntRect? area;       // screen rect covered, null while hidden
    bool showing;
    Dictionary<nint, IntRect> shownLayout = [];

    public OverlayWindow()
    {
        Title = "Tiler overlay";
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        Topmost = true;
        ShowInTaskbar = false;
        ShowActivated = false;
        Focusable = false;
        Left = -32000;
        Top = -32000;
        Width = 1;
        Height = 1;
        canvas.Children.Add(dim);
        Content = canvas;
        DpiChanged += (_, _) => ApplyScale();

        hwnd = new WindowInteropHelper(this).EnsureHandle();
        SetWindowLong(hwnd, GWL_EXSTYLE,
            GetWindowLong(hwnd, GWL_EXSTYLE) | WS_EX_TRANSPARENT | WS_EX_LAYERED | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE);
        HwndSource.FromHwnd(hwnd).AddHook(WndProc);
    }

    /// <param name="workArea">Monitor area to cover.</param>
    /// <param name="layout">Where every window would end up.</param>
    /// <param name="dragged">The window being dropped; its ghost is highlighted.</param>
    public void ShowPreview(IntRect workArea, Dictionary<nint, IntRect> layout, nint dragged)
    {
        if (showing && area == workArea && SameLayout(layout, shownLayout))
            return;

        if (area != workArea)
        {
            // Another monitor: start over there.
            RemoveGhosts(ghosts.Keys.ToList());
            area = workArea;
            SetWindowPos(hwnd, HWND_TOPMOST, workArea.Left, workArea.Top, workArea.Width, workArea.Height, SWP_NOACTIVATE);
            ApplyScale();
        }
        shownLayout = layout;

        RemoveGhosts(ghosts.Keys.Where(w => !layout.ContainsKey(w)).ToList());
        foreach (var (window, rect) in layout)
        {
            var target = rect.Offset(-workArea.Left, -workArea.Top);
            if (!ghosts.TryGetValue(window, out var ghost))
            {
                ghost = CreateGhost(window, window == dragged);
                ghosts[window] = ghost;
                canvas.Children.Add(ghost);
                // Take off from where the window is now, so the preview shows the whole move.
                var start = WindowOps.VisualRect(window) is { } now ? now.Offset(-workArea.Left, -workArea.Top) : target;
                Place(ghost, start);
            }
            MorphTo(ghost, target);
        }

        if (!showing)
        {
            showing = true;
            if (!IsVisible)
                Show();
            canvas.BeginAnimation(OpacityProperty, new DoubleAnimation(1, FadeDuration) { EasingFunction = Ease });
        }
    }

    public void HidePreview()
    {
        if (!showing)
            return;
        showing = false;
        var fade = new DoubleAnimation(0, FadeDuration) { EasingFunction = Ease };
        fade.Completed += (_, _) =>
        {
            if (showing)
                return;
            Hide();
            RemoveGhosts(ghosts.Keys.ToList());
            shownLayout = [];
            area = null;
        };
        canvas.BeginAnimation(OpacityProperty, fade);
    }

    Border CreateGhost(nint window, bool dragged)
    {
        double u = UnitScale();
        return new Border
        {
            CornerRadius = new CornerRadius(8 * u),
            BorderThickness = new Thickness((dragged ? 2 : 1) * u),
            BorderBrush = dragged ? Brush(0xF0, accent) : Brush(0xA0, Colors.White),
            Background = dragged ? Brush(0x70, accent) : Brush(0x30, Colors.White),
            Child = new TextBlock
            {
                Text = WindowOps.Title(window),
                Foreground = Brush(dragged ? (byte)0xFF : (byte)0xC0, Colors.White),
                FontSize = 13 * u,
                FontWeight = dragged ? FontWeights.SemiBold : FontWeights.Normal,
                TextTrimming = TextTrimming.CharacterEllipsis,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(12 * u),
                Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 6 * u, ShadowDepth = 0, Opacity = 0.8 },
            },
        };
    }

    static void Place(FrameworkElement element, IntRect r)
    {
        Canvas.SetLeft(element, r.Left);
        Canvas.SetTop(element, r.Top);
        element.Width = Math.Max(0, r.Width);
        element.Height = Math.Max(0, r.Height);
    }

    static void MorphTo(FrameworkElement element, IntRect r)
    {
        Animate(element, Canvas.LeftProperty, r.Left);
        Animate(element, Canvas.TopProperty, r.Top);
        Animate(element, WidthProperty, Math.Max(0, r.Width));
        Animate(element, HeightProperty, Math.Max(0, r.Height));
    }

    static void Animate(FrameworkElement element, DependencyProperty property, double to) =>
        element.BeginAnimation(property, new DoubleAnimation(to, MorphDuration) { EasingFunction = Ease });

    void RemoveGhosts(List<nint> windows)
    {
        foreach (var window in windows)
        {
            if (ghosts.Remove(window, out var ghost))
                canvas.Children.Remove(ghost);
        }
    }

    /// <summary>Undo WPF's DPI scaling so canvas units are physical pixels.</summary>
    void ApplyScale()
    {
        double wpfScale = VisualTreeHelper.GetDpi(this).DpiScaleX;
        canvas.LayoutTransform = new ScaleTransform(1 / wpfScale, 1 / wpfScale);
        if (area is { } a)
        {
            dim.Width = a.Width;
            dim.Height = a.Height;
        }
    }

    /// <summary>Physical pixels per 96-DPI pixel on the monitor the overlay is on; sizes strokes and text.</summary>
    double UnitScale()
    {
        uint dpi = GetDpiForWindow(hwnd);
        return dpi > 0 ? dpi / 96.0 : 1;
    }

    nint WndProc(nint h, int msg, nint wParam, nint lParam, ref bool handled)
    {
        if (msg == WM_WINDOWPOSCHANGING && area is { } a)
        {
            // WPF resizes the window by itself when it crosses into a monitor with another DPI;
            // keep it pinned to the work area instead.
            var pos = Marshal.PtrToStructure<WINDOWPOS>(lParam);
            if ((pos.flags & SWP_NOMOVE) == 0)
            {
                pos.x = a.Left;
                pos.y = a.Top;
            }
            if ((pos.flags & SWP_NOSIZE) == 0)
            {
                pos.cx = a.Width;
                pos.cy = a.Height;
            }
            Marshal.StructureToPtr(pos, lParam, false);
        }
        else if (msg == WM_MOUSEACTIVATE)
        {
            handled = true;
            return MA_NOACTIVATE;
        }
        return 0;
    }

    static bool SameLayout(Dictionary<nint, IntRect> a, Dictionary<nint, IntRect> b) =>
        a.Count == b.Count && a.All(kv => b.TryGetValue(kv.Key, out var r) && r == kv.Value);

    static SolidColorBrush Brush(byte alpha, Color color)
    {
        var brush = new SolidColorBrush(Color.FromArgb(alpha, color.R, color.G, color.B));
        brush.Freeze();
        return brush;
    }

    static T Freeze<T>(T freezable) where T : Freezable
    {
        freezable.Freeze();
        return freezable;
    }

    /// <summary>The Windows accent color; the registry stores it as 0xAABBGGRR.</summary>
    static Color ReadAccentColor()
    {
        if (Registry.GetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\DWM", "AccentColor", null) is int abgr)
            return Color.FromRgb((byte)abgr, (byte)(abgr >> 8), (byte)(abgr >> 16));
        return FallbackAccent;
    }
}
