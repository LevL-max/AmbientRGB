using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using Microsoft.Win32;

namespace AmbientRgb
{
    sealed class TrayApp : ApplicationContext
    {
        readonly Settings settings;
        readonly Engine engine;
        readonly NotifyIcon tray;
        readonly ToolStripMenuItem pauseItem;
        readonly Timer timer;
        SettingsForm form;
        IntPtr iconHandle;
        int iconRgb = -1;

        public TrayApp(Settings s, Engine e)
        {
            settings = s;
            engine = e;

            var menu = new ContextMenuStrip();
            var settingsItem = new ToolStripMenuItem("Settings…", null, delegate { ShowSettings(); });
            settingsItem.Font = new Font(settingsItem.Font, FontStyle.Bold);
            menu.Items.Add(settingsItem);
            pauseItem = new ToolStripMenuItem("Pause (background color only)", null, delegate { TogglePause(); });
            pauseItem.Checked = settings.Paused;
            menu.Items.Add(pauseItem);
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(new ToolStripMenuItem("Exit", null, delegate { ExitThread(); }));

            tray = new NotifyIcon();
            tray.ContextMenuStrip = menu;
            tray.Text = "Ambient RGB";
            tray.MouseClick += (sender, ev) => { if (ev.Button == MouseButtons.Left) ShowSettings(); };
            UpdateIcon();
            tray.Visible = true;

            timer = new Timer();
            timer.Interval = 400;
            timer.Tick += delegate { UpdateIcon(); };
            timer.Start();
        }

        void TogglePause()
        {
            settings.Paused = !settings.Paused;
            pauseItem.Checked = settings.Paused;
            settings.Save();
            if (form != null) form.SyncFromSettings();
        }

        void ShowSettings()
        {
            if (form == null || form.IsDisposed)
            {
                form = new SettingsForm(settings, engine);
                form.FormClosed += delegate { settings.Save(); pauseItem.Checked = settings.Paused; form = null; };
                form.Show();
            }
            if (form.WindowState == FormWindowState.Minimized) form.WindowState = FormWindowState.Normal;
            form.Activate();
        }

        void UpdateIcon()
        {
            int rgb = engine.OutputRgb;
            int quantized = rgb & 0xF0F0F0;
            if (quantized == iconRgb) return;
            iconRgb = quantized;

            using (var bmp = new Bitmap(32, 32))
            {
                using (var g = Graphics.FromImage(bmp))
                {
                    g.SmoothingMode = SmoothingMode.AntiAlias;
                    g.Clear(Color.Transparent);
                    using (var brush = new SolidBrush(UiColors.Visible(rgb))) g.FillEllipse(brush, 3, 3, 26, 26);
                    using (var pen = new Pen(Color.FromArgb(220, 20, 20, 20), 2.5f)) g.DrawEllipse(pen, 3, 3, 26, 26);
                }
                IntPtr h = bmp.GetHicon();
                tray.Icon = Icon.FromHandle(h);
                if (iconHandle != IntPtr.Zero) DestroyIcon(iconHandle);
                iconHandle = h;
            }
            tray.Text = settings.Paused ? "Ambient RGB — paused" : "Ambient RGB";
        }

        protected override void ExitThreadCore()
        {
            timer.Stop();
            if (form != null && !form.IsDisposed) form.Close();
            tray.Visible = false;
            tray.Dispose();
            if (iconHandle != IntPtr.Zero) DestroyIcon(iconHandle);
            base.ExitThreadCore();
        }

        [DllImport("user32.dll")] static extern bool DestroyIcon(IntPtr handle);
    }

    static class UiColors
    {
        public static Color FromRgb(int rgb)
        {
            return Color.FromArgb((rgb >> 16) & 0xFF, (rgb >> 8) & 0xFF, rgb & 0xFF);
        }

        /// <summary>Converts a linear LED value back to sRGB so the on-screen preview looks like the LEDs.</summary>
        public static Color FromLed(int rgb)
        {
            return Color.FromArgb(ToSrgb((rgb >> 16) & 0xFF), ToSrgb((rgb >> 8) & 0xFF), ToSrgb(rgb & 0xFF));
        }

        static int ToSrgb(int v)
        {
            return (int)Math.Round(255.0 * Math.Pow(v / 255.0, 1.0 / 2.2));
        }

        /// <summary>Same hue at full brightness (for a linear LED value), so a dim color still reads as a swatch.</summary>
        public static Color Visible(int ledRgb)
        {
            Color c = FromLed(ledRgb);
            int r = c.R, g = c.G, b = c.B;
            int m = Math.Max(r, Math.Max(g, b));
            if (m < 8) return Color.FromArgb(40, 40, 40);
            return Color.FromArgb(r * 255 / m, g * 255 / m, b * 255 / m);
        }
    }

    static class Autostart
    {
        const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        const string ValueName = "AmbientRGB";

        public static bool IsEnabled()
        {
            using (var k = Registry.CurrentUser.OpenSubKey(RunKey))
                return k != null && k.GetValue(ValueName) != null;
        }

        public static void Set(bool enabled)
        {
            using (var k = Registry.CurrentUser.CreateSubKey(RunKey))
            {
                if (enabled) k.SetValue(ValueName, "\"" + Application.ExecutablePath + "\"");
                else k.DeleteValue(ValueName, false);
            }
        }
    }
}
