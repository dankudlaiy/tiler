using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;
using Microsoft.Win32;
using static Tiler.NativeMethods;

namespace Tiler;

internal sealed class TrayIcon : IDisposable
{
    static readonly int[] Gaps = [0, 4, 8, 12, 16];
    static readonly (string Text, int Ms)[] Animations = [("Выключена", 0), ("Быстрая", 150), ("Обычная", 220), ("Плавная", 350)];
    static readonly (string Text, ModifierKey Key)[] FloatKeys = [("Ctrl", ModifierKey.Ctrl), ("Shift", ModifierKey.Shift), ("Alt", ModifierKey.Alt)];

    readonly NotifyIcon notifyIcon;
    readonly Icon icon;
    readonly nint iconHandle;

    public TrayIcon(Settings settings, TilerController controller, bool elevated, Action exit)
    {
        icon = CreateIcon(out iconHandle);

        var menu = new ContextMenuStrip();
        menu.Items.Add(new ToolStripMenuItem(elevated ? "Tiler" : "Tiler — без прав администратора") { Enabled = false });
        menu.Items.Add(new ToolStripSeparator());

        var pause = new ToolStripMenuItem("Пауза") { CheckOnClick = true };
        pause.Click += (_, _) => controller.Paused = pause.Checked;
        menu.Items.Add(pause);
        menu.Items.Add("Разложить все окна заново", null, (_, _) => controller.Retile());
        menu.Items.Add(new ToolStripSeparator());

        menu.Items.Add(Choice("Отступ", Gaps, px => $"{px} px", px => settings.Gap == px, px =>
        {
            controller.SetGap(px);
            settings.Save();
        }));
        menu.Items.Add(Choice("Анимация", Animations, a => a.Text, a => settings.AnimationMs == a.Ms, a =>
        {
            controller.SetAnimation(a.Ms);
            settings.Save();
        }));
        menu.Items.Add(Choice("Сделать окно плавающим: бросить с", FloatKeys, k => k.Text, k => settings.FloatModifier == k.Key, k =>
        {
            settings.FloatModifier = k.Key;
            settings.Save();
        }));

        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Открыть папку с настройками и логом", null, (_, _) =>
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{Settings.Folder}\"") { UseShellExecute = true }));
        menu.Items.Add("Выход", null, (_, _) => exit());

        notifyIcon = new NotifyIcon
        {
            Icon = icon,
            Text = "Tiler",
            ContextMenuStrip = menu,
            Visible = true,
        };
    }

    /// <summary>A submenu of mutually exclusive options; the current one is checked each time it opens.</summary>
    static ToolStripMenuItem Choice<T>(string text, T[] options, Func<T, string> label, Func<T, bool> isCurrent, Action<T> select)
    {
        var parent = new ToolStripMenuItem(text);
        foreach (var option in options)
            parent.DropDownItems.Add(new ToolStripMenuItem(label(option), null, (_, _) => select(option)));
        parent.DropDownOpening += (_, _) =>
        {
            for (int i = 0; i < options.Length; i++)
                ((ToolStripMenuItem)parent.DropDownItems[i]).Checked = isCurrent(options[i]);
        };
        return parent;
    }

    public void Dispose()
    {
        notifyIcon.Visible = false;
        notifyIcon.Dispose();
        icon.Dispose();
        DestroyIcon(iconHandle);
    }

    /// <summary>Three tiles, light or dark to match the taskbar.</summary>
    static Icon CreateIcon(out nint handle)
    {
        using var bitmap = new Bitmap(32, 32);
        using (var g = Graphics.FromImage(bitmap))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Color.Transparent);
            using var brush = new SolidBrush(IsTaskbarLight() ? Color.FromArgb(0x20, 0x20, 0x20) : Color.White);
            FillRounded(g, brush, 3, 4, 12, 24);
            FillRounded(g, brush, 17, 4, 12, 11);
            FillRounded(g, brush, 17, 17, 12, 11);
        }
        handle = bitmap.GetHicon();
        return Icon.FromHandle(handle);
    }

    static void FillRounded(Graphics g, Brush brush, int x, int y, int width, int height)
    {
        const int d = 6;
        using var path = new GraphicsPath();
        path.AddArc(x, y, d, d, 180, 90);
        path.AddArc(x + width - d, y, d, d, 270, 90);
        path.AddArc(x + width - d, y + height - d, d, d, 0, 90);
        path.AddArc(x, y + height - d, d, d, 90, 90);
        path.CloseFigure();
        g.FillPath(brush, path);
    }

    static bool IsTaskbarLight() =>
        Registry.GetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize", "SystemUsesLightTheme", 0) is 1;
}
