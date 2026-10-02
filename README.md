# Tiler

Automatic window tiling for Windows 10/11 in the spirit of [komorebi](https://github.com/LGUG2Z/komorebi): every regular window on a monitor gets its own tile, and where a window lands is decided by where you drop it.

<!-- TODO: add a short GIF of drag-to-tile here (docs/demo.gif) -->

## Features

- **Drag to tile.** Drop a window near the edge of another tile — it takes that side and the tile splits in half. Drop it in the center of another tile — the two windows swap. Drop it over its own tile — it goes back.
  While you drag, "ghosts" of the future layout are drawn over the monitor and smoothly follow the cursor.
- **Maximized and full-screen windows** are left alone: maximize one and the others close the gap. Drag a maximized window and it is restored and placed where you drop it.
- **New, restored and shown windows** join the layout automatically; closed and minimized ones leave it and their neighbours close the gap.
- **Resizing.** Drag the edge of a tile and the shared border with its neighbours moves.
- **Windows smaller than their tile** (tray option). A window smaller than its tile is centered in it. Windows joining the layout keep their size (a window larger than its tile shrinks to fit). Shrink a window and it stays that size while its neighbours stay put; drag its edge past the tile and the shared border moves as usual; stretch it to the full tile and it fills the tile again. The size is kept when the window is dragged, minimized or moved to another monitor.
- **Minimum sizes.** Tiles never get smaller than a window is willing to shrink (`WM_GETMINMAXINFO`). If there is no room beside a neighbour, the window goes above or below it, and a new window goes to another tile that has space. Windows that silently refuse to shrink are detected after the first attempt and re-placed.
- **Floating windows.** Drop a window while holding **Ctrl** and it leaves the layout. A normal drag puts it back.
- **Esc** while dragging cancels.
- Everything is animated; windows glide to their tiles.

Not tiled: dialogs, always-on-top windows (picture-in-picture etc.), non-resizable windows, windows on other virtual desktops.

Tray menu: pause, re-tile, gap size, smaller-than-tile windows, animation speed, floating-window modifier key, open log folder.
Settings and the log live in `%AppData%\Tiler`.

Windows' built-in Snap interferes when dragging to a screen edge; you can turn it off in
*Settings → System → Multitasking → Snap windows*.

## Running

```bash
dotnet run --project src/Tiler
```

On start Tiler asks for administrator rights — otherwise Windows won't let it move windows of elevated programs.

- `--no-elevate` — run without administrator rights.
- `--only-title PREFIX` — development sandbox: only windows whose title starts with the prefix are tiled; can run next to a normal instance.

```bash
dotnet test
```

## How it works

- `src/Tiler.Core` — logic with no Win32 dependencies: `Workspace` is the split tree of one monitor (add a window at a point, move, remove, resize), `DropZones` computes the drop targets. Covered by xUnit tests in `tests/Tiler.Core.Tests`.
- `src/Tiler` — WPF tray application (.NET 10):
  - `TilerController` — `SetWinEventHook` hooks (drag, show/hide, minimize, location change), one `Workspace` per monitor;
  - `OverlayWindow` — transparent overlay that draws the layout ghosts;
  - `Animator` — smooth window movement, one thread per window and one `SetWindowPos` per frame;
  - `WindowOps` — decides which windows to tile and corrects for the invisible frame (`DWMWA_EXTENDED_FRAME_BOUNDS`).

## Tech

C# · .NET 10 · WPF · Win32 interop (P/Invoke: `SetWinEventHook`, `SetWindowPos`, DWM) · per-monitor DPI awareness · xUnit
