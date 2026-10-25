using System;
using System.Drawing;
using System.Windows.Forms;

namespace AmbientRgb
{
    sealed class SettingsForm : Form
    {
        readonly Settings s;
        readonly Engine e;
        readonly Timer timer;
        bool syncing;
        int saveTick;

        Button baseButton, flashButton;
        TrackBar idleBar, screenBar, smoothBar, audioBar, flashBar, pulseBar;
        Label idleValue, screenValue, smoothValue, audioValue, flashValue, pulseValue;
        CheckBox fullscreenBox, pauseBox, keyboardBox, autostartBox;
        SwatchPanel outputSwatch, screenSwatch;
        MeterPanel meter;
        Label statusLabel;

        public SettingsForm(Settings settings, Engine engine)
        {
            s = settings;
            e = engine;

            Text = "Ambient RGB";
            FormBorderStyle = FormBorderStyle.FixedSingle;
            MaximizeBox = false;
            StartPosition = FormStartPosition.CenterScreen;
            Font = new Font("Segoe UI", 9.5f);
            AutoScaleMode = AutoScaleMode.Dpi;
            AutoSize = true;
            AutoSizeMode = AutoSizeMode.GrowAndShrink;
            Padding = new Padding(14);
            ShowInTaskbar = true;

            var root = new TableLayoutPanel { ColumnCount = 1, AutoSize = true, Dock = DockStyle.Fill };
            Controls.Add(root);

            // ---- live preview ----
            var preview = new TableLayoutPanel { ColumnCount = 3, AutoSize = true, Margin = new Padding(0, 0, 0, 10) };
            preview.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 150));
            preview.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 150));
            preview.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 150));
            preview.Controls.Add(Caption("Lights now"), 0, 0);
            preview.Controls.Add(Caption("Screen color"), 1, 0);
            preview.Controls.Add(Caption("Audio / flash"), 2, 0);
            outputSwatch = new SwatchPanel { Size = new Size(140, 46), Margin = new Padding(3, 3, 6, 3) };
            screenSwatch = new SwatchPanel { Size = new Size(140, 46), Margin = new Padding(3, 3, 6, 3) };
            meter = new MeterPanel { Size = new Size(140, 46), Margin = new Padding(3) };
            preview.Controls.Add(outputSwatch, 0, 1);
            preview.Controls.Add(screenSwatch, 1, 1);
            preview.Controls.Add(meter, 2, 1);
            root.Controls.Add(preview);

            // ---- settings grid ----
            var grid = new TableLayoutPanel { ColumnCount = 3, AutoSize = true };
            grid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 190));
            grid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 220));
            grid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 50));
            root.Controls.Add(grid);

            baseButton = ColorButton();
            baseButton.Click += delegate { PickColor(true); };
            AddRow(grid, "Background color", baseButton, null);

            idleBar = Bar(5, 100); idleValue = ValueLabel();
            idleBar.ValueChanged += delegate { if (!syncing) s.IdleBrightness = idleBar.Value; Refresh(idleValue, idleBar.Value + "%"); };
            AddRow(grid, "Brightness", idleBar, idleValue);

            screenBar = Bar(0, 100); screenValue = ValueLabel();
            screenBar.ValueChanged += delegate { if (!syncing) s.ScreenInfluence = screenBar.Value; Refresh(screenValue, screenBar.Value == 0 ? "off" : screenBar.Value + "%"); };
            AddRow(grid, "Screen reaction", screenBar, screenValue);

            smoothBar = Bar(0, 100); smoothValue = ValueLabel();
            smoothBar.ValueChanged += delegate { if (!syncing) s.Smoothness = smoothBar.Value; Refresh(smoothValue, smoothBar.Value + "%"); };
            AddRow(grid, "Smoothing", smoothBar, smoothValue);

            audioBar = Bar(0, 100); audioValue = ValueLabel();
            audioBar.ValueChanged += delegate { if (!syncing) s.AudioSensitivity = audioBar.Value; Refresh(audioValue, audioBar.Value == 0 ? "off" : audioBar.Value + "%"); };
            AddRow(grid, "Audio sensitivity", audioBar, audioValue);

            flashBar = Bar(0, 100); flashValue = ValueLabel();
            flashBar.ValueChanged += delegate { if (!syncing) s.FlashStrength = flashBar.Value; Refresh(flashValue, flashBar.Value + "%"); };
            AddRow(grid, "Flash strength", flashBar, flashValue);

            pulseBar = Bar(0, 100); pulseValue = ValueLabel();
            pulseBar.ValueChanged += delegate { if (!syncing) s.MusicPulse = pulseBar.Value; Refresh(pulseValue, pulseBar.Value == 0 ? "off" : pulseBar.Value + "%"); };
            AddRow(grid, "Music pulse", pulseBar, pulseValue);

            flashButton = ColorButton();
            flashButton.Click += delegate { PickColor(false); };
            AddRow(grid, "Flash color", flashButton, null);

            fullscreenBox = new CheckBox { Text = "Follow the screen only in fullscreen apps (games, video)", AutoSize = true, Margin = new Padding(3, 8, 3, 2) };
            fullscreenBox.CheckedChanged += delegate { if (!syncing) s.FullscreenOnly = fullscreenBox.Checked; };
            pauseBox = new CheckBox { Text = "Pause — background color only", AutoSize = true, Margin = new Padding(3, 2, 3, 2) };
            pauseBox.CheckedChanged += delegate { if (!syncing) s.Paused = pauseBox.Checked; };
            keyboardBox = new CheckBox { Text = "AULA keyboard (USB cable only)", AutoSize = true, Margin = new Padding(3, 2, 3, 2) };
            keyboardBox.CheckedChanged += delegate { if (!syncing) s.KeyboardEnabled = keyboardBox.Checked; };
            autostartBox = new CheckBox { Text = "Start with Windows", AutoSize = true, Margin = new Padding(3, 2, 3, 2) };
            autostartBox.CheckedChanged += delegate
            {
                if (syncing) return;
                try { Autostart.Set(autostartBox.Checked); }
                catch (Exception ex) { MessageBox.Show(this, "Could not change autostart: " + ex.Message, Text); }
            };
            root.Controls.Add(fullscreenBox);
            root.Controls.Add(pauseBox);
            root.Controls.Add(keyboardBox);
            root.Controls.Add(autostartBox);

            statusLabel = new Label { AutoSize = true, ForeColor = SystemColors.GrayText, Margin = new Padding(3, 10, 3, 6), MaximumSize = new Size(470, 0) };
            root.Controls.Add(statusLabel);

            var buttons = new FlowLayoutPanel { FlowDirection = FlowDirection.RightToLeft, AutoSize = true, Dock = DockStyle.Fill, Margin = new Padding(0) };
            var close = new Button { Text = "Close", AutoSize = true, Padding = new Padding(10, 2, 10, 2) };
            close.Click += delegate { Close(); };
            var reset = new Button { Text = "Defaults", AutoSize = true, Padding = new Padding(10, 2, 10, 2) };
            reset.Click += delegate { s.ResetToDefaults(); SyncFromSettings(); };
            buttons.Controls.Add(close);
            buttons.Controls.Add(reset);
            root.Controls.Add(buttons);
            CancelButton = close;

            SyncFromSettings();

            timer = new Timer { Interval = 40 };
            timer.Tick += delegate { UpdateLive(); };
            timer.Start();
            FormClosed += delegate { timer.Stop(); };
        }

        public void SyncFromSettings()
        {
            syncing = true;
            try
            {
                baseButton.BackColor = UiColors.FromRgb(s.BaseColor);
                flashButton.BackColor = UiColors.FromRgb(s.FlashColor);
                idleBar.Value = Math.Max(idleBar.Minimum, s.IdleBrightness);
                screenBar.Value = s.ScreenInfluence;
                smoothBar.Value = s.Smoothness;
                audioBar.Value = s.AudioSensitivity;
                flashBar.Value = s.FlashStrength;
                pulseBar.Value = s.MusicPulse;
                fullscreenBox.Checked = s.FullscreenOnly;
                pauseBox.Checked = s.Paused;
                keyboardBox.Checked = s.KeyboardEnabled;
                bool auto = false;
                try { auto = Autostart.IsEnabled(); } catch { }
                autostartBox.Checked = auto;
            }
            finally { syncing = false; }
        }

        void UpdateLive()
        {
            outputSwatch.Color = UiColors.FromLed(e.OutputRgb);
            if (++saveTick % 25 == 0) s.SaveIfChanged();
            bool screenOn = e.ScreenActive;
            screenSwatch.Color = screenOn && e.Vivid > 0.02f ? UiColors.FromRgb(e.ScreenRgb) : Color.FromArgb(45, 45, 45);
            screenSwatch.Caption = s.Paused ? "paused"
                : !screenOn ? (s.FullscreenOnly ? "not fullscreen" : "off")
                : (int)(e.Vivid * 100) + "% vivid";
            meter.Level = e.AudioPeak;
            meter.Flash = e.Flash;
            outputSwatch.Invalidate();
            screenSwatch.Invalidate();
            meter.Invalidate();
            if (pauseBox.Checked != s.Paused) SyncFromSettings();

            string status = e.OpenRgbStatus + "\n" + e.ScreenStatus + "  (" + e.CaptureFps + " fps)\n" + e.AudioStatus + "\n" + e.KeyboardStatus;
            if (statusLabel.Text != status) statusLabel.Text = status;
        }

        void PickColor(bool isBase)
        {
            using (var dlg = new ColorDialog { FullOpen = true, AnyColor = true })
            {
                dlg.Color = UiColors.FromRgb(isBase ? s.BaseColor : s.FlashColor);
                if (dlg.ShowDialog(this) != DialogResult.OK) return;
                int rgb = (dlg.Color.R << 16) | (dlg.Color.G << 8) | dlg.Color.B;
                if (isBase) { s.BaseColor = rgb; baseButton.BackColor = dlg.Color; }
                else { s.FlashColor = rgb; flashButton.BackColor = dlg.Color; }
                s.Save();
            }
        }

        static void Refresh(Label l, string text) { l.Text = text; }

        static void AddRow(TableLayoutPanel grid, string label, Control control, Control value)
        {
            int row = grid.RowCount++;
            grid.Controls.Add(new Label { Text = label, AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(3, 6, 3, 6) }, 0, row);
            grid.Controls.Add(control, 1, row);
            if (value != null) grid.Controls.Add(value, 2, row);
        }

        static Label Caption(string text)
        {
            return new Label { Text = text, AutoSize = true, ForeColor = SystemColors.GrayText, Margin = new Padding(3, 0, 3, 0) };
        }

        static Label ValueLabel()
        {
            return new Label { AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(3, 6, 3, 6) };
        }

        static TrackBar Bar(int min, int max)
        {
            return new TrackBar
            {
                Minimum = min, Maximum = max, TickFrequency = 10, SmallChange = 1, LargeChange = 10,
                AutoSize = false, Size = new Size(214, 30), TickStyle = TickStyle.None, Margin = new Padding(3, 3, 3, 3)
            };
        }

        static Button ColorButton()
        {
            var b = new Button { Size = new Size(120, 28), FlatStyle = FlatStyle.Flat, Margin = new Padding(3, 4, 3, 4), Cursor = Cursors.Hand };
            b.FlatAppearance.BorderColor = Color.FromArgb(90, 90, 90);
            return b;
        }
    }

    class SwatchPanel : Panel
    {
        public Color Color = Color.Black;
        public string Caption;

        public SwatchPanel()
        {
            DoubleBuffered = true;
            ResizeRedraw = true;
        }

        protected override void OnPaint(PaintEventArgs pe)
        {
            var g = pe.Graphics;
            using (var b = new SolidBrush(Color)) g.FillRectangle(b, ClientRectangle);
            using (var p = new Pen(Color.FromArgb(90, 90, 90))) g.DrawRectangle(p, 0, 0, Width - 1, Height - 1);
            string text = Caption ?? ("#" + Color.R.ToString("X2") + Color.G.ToString("X2") + Color.B.ToString("X2"));
            bool dark = Color.R * 0.3 + Color.G * 0.59 + Color.B * 0.11 < 110;
            TextRenderer.DrawText(g, text, Font, ClientRectangle, dark ? Color.White : Color.Black,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
        }
    }

    class MeterPanel : Panel
    {
        public float Level, Flash;

        public MeterPanel()
        {
            DoubleBuffered = true;
            ResizeRedraw = true;
        }

        protected override void OnPaint(PaintEventArgs pe)
        {
            var g = pe.Graphics;
            g.Clear(Color.FromArgb(30, 30, 30));
            int w = (int)((Width - 2) * Math.Min(1.0, Math.Sqrt(Math.Max(0, Level))));
            using (var b = new SolidBrush(Color.FromArgb(70, 200, 120))) g.FillRectangle(b, 1, Height / 2, w, Height / 2 - 1);
            int a = (int)(255 * Math.Min(1f, Flash));
            if (a > 0) using (var b = new SolidBrush(Color.FromArgb(a, 255, 240, 200))) g.FillRectangle(b, 1, 1, Width - 2, Height / 2 - 1);
            using (var p = new Pen(Color.FromArgb(90, 90, 90))) g.DrawRectangle(p, 0, 0, Width - 1, Height - 1);
        }
    }
}
