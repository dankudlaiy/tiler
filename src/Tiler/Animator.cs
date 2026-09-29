using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Tiler.Core;
using static Tiler.NativeMethods;

namespace Tiler;

/// <summary>
/// Glides windows to their tiles. Each window is animated on its own thread with blocking
/// SetWindowPos calls, one per display frame: a slow app simply gets fewer frames instead of
/// piling up queued moves and lagging behind the others.
/// </summary>
internal sealed class Animator
{
    // Apps that change DPI or enforce a minimum size adjust themselves right after a move; check once more after that.
    static readonly TimeSpan SettleDelay = TimeSpan.FromMilliseconds(150);

    readonly Dictionary<nint, Motion> motions = []; // UI thread only

    public int DurationMs { get; set; }

    public void Animate(IReadOnlyDictionary<nint, IntRect> targets)
    {
        foreach (var (hwnd, to) in targets)
        {
            if (!IsWindow(hwnd) || IsIconic(hwnd) || IsZoomed(hwnd))
                continue;

            // A window already in motion continues from where it is now, so retargeting stays smooth.
            IntRect? from = null;
            if (motions.Remove(hwnd, out var running) && !running.Finished)
            {
                running.Cancel();
                from = running.Current;
            }
            from ??= WindowOps.VisualRect(hwnd);
            if (from is not { } start || start == to)
                continue;

            if (DurationMs <= 0)
            {
                WindowOps.MoveVisual(hwnd, to);
                continue;
            }

            var motion = new Motion(hwnd, start, to, DurationMs);
            motions[hwnd] = motion;
            motion.Start();
        }
    }

    /// <summary>Stops animating the window, e.g. because the user grabbed it.</summary>
    public void Cancel(nint hwnd)
    {
        if (motions.Remove(hwnd, out var motion))
            motion.Cancel();
    }

    sealed class Motion(nint hwnd, IntRect start, IntRect to, int durationMs)
    {
        readonly CancellationTokenSource cancel = new();
        readonly object sync = new();
        readonly IntRect from = start;
        IntRect current = start;

        public IntRect Current
        {
            get
            {
                lock (sync)
                    return current;
            }
        }

        volatile bool finished;

        public bool Finished => finished;

        public void Cancel() => cancel.Cancel();

        public void Start() => Task.Factory.StartNew(Run, TaskCreationOptions.LongRunning);

        void Run()
        {
            try
            {
                // The frame stays the same while the window moves within one monitor.
                var frame = WindowOps.FrameOf(hwnd);
                var clock = Stopwatch.StartNew();
                while (!cancel.IsCancellationRequested)
                {
                    if (!IsWindow(hwnd))
                        return;
                    if (IsHungAppWindow(hwnd))
                    {
                        // Queue the final position; it lands whenever the app wakes up.
                        WindowOps.MoveVisual(hwnd, to);
                        return;
                    }

                    double t = Math.Min(1, clock.Elapsed.TotalMilliseconds / durationMs);
                    var rect = Lerp(from, to, EaseOutCubic(t));
                    lock (sync)
                        current = rect;
                    WindowOps.MoveVisual(hwnd, rect, frame, async: false);
                    if (t >= 1)
                        break;
                    if (DwmFlush() != 0)
                        Thread.Sleep(8);
                }

                if (cancel.Token.WaitHandle.WaitOne(SettleDelay))
                    return;
                if (IsWindow(hwnd) && !IsZoomed(hwnd) && !IsIconic(hwnd) && WindowOps.VisualRect(hwnd) != to)
                    WindowOps.MoveVisual(hwnd, to, async: false);
            }
            catch (Exception e)
            {
                Log.Error($"Animation of 0x{hwnd:X}", e);
            }
            finally
            {
                finished = true;
            }
        }

        static double EaseOutCubic(double t) => 1 - Math.Pow(1 - t, 3);

        static IntRect Lerp(IntRect a, IntRect b, double t) => new(
            (int)Math.Round(a.Left + (b.Left - a.Left) * t),
            (int)Math.Round(a.Top + (b.Top - a.Top) * t),
            (int)Math.Round(a.Right + (b.Right - a.Right) * t),
            (int)Math.Round(a.Bottom + (b.Bottom - a.Bottom) * t));
    }
}
