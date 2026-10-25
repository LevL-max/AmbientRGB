using System;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Media;
using System.Threading;
using System.Windows.Forms;

namespace AmbientRgb
{
    static class Program
    {
        [STAThread]
        static int Main(string[] args)
        {
            Log.Init();
#if DIAG
            return Diag.Run(args);
#else
            bool created;
            using (var mutex = new Mutex(true, "Local\\AmbientRGB.SingleInstance", out created))
            {
                if (!created) return 0;
                Log.Write("AmbientRGB started");
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);

                var settings = Settings.Load();
                var engine = new Engine(settings);
                engine.Start();
                Microsoft.Win32.SystemEvents.SessionEnding += delegate { engine.Stop(); settings.Save(); };
                try { Application.Run(new TrayApp(settings, engine)); }
                finally
                {
                    engine.Stop();
                    settings.Save();
                    Log.Write("AmbientRGB stopped");
                }
            }
            return 0;
#endif
        }
    }

#if DIAG
    /// <summary>Console diagnostics: AmbientRGB-diag.exe [seconds] [--selftest]</summary>
    static class Diag
    {
        public static int Run(string[] args)
        {
            Log.Echo = true;
            int seconds = 10;
            bool selftest = false;
            foreach (string a in args)
            {
                int n;
                if (a == "--selftest") selftest = true;
                else if (int.TryParse(a, out n)) seconds = n;
            }

            var settings = Settings.Load();
            var engine = new Engine(settings);
            engine.Start();
            var start = DateTime.Now;
            try
            {
                Pump(engine, 3.0);
                if (selftest) SelfTest(engine);
                while ((DateTime.Now - start).TotalSeconds < seconds) Pump(engine, 0.5);
            }
            finally { engine.Stop(); }
            return 0;
        }

        static void SelfTest(Engine engine)
        {
            Log.Write("=== SELFTEST: full-screen RED");
            using (var f = ColorWindow(Color.FromArgb(230, 20, 20)))
            {
                f.Show();
                Pump(engine, 2.0);
                Log.Write("=== SELFTEST: full-screen BLUE");
                f.BackColor = Color.FromArgb(20, 60, 240);
                f.Refresh();
                Pump(engine, 2.0);
                Log.Write("=== SELFTEST: full-screen GRAY (expect background color)");
                f.BackColor = Color.FromArgb(128, 128, 128);
                f.Refresh();
                Pump(engine, 2.5);
            }
            Log.Write("=== SELFTEST: sound bursts (expect flash > 0)");
            using (var ms = new MemoryStream(NoiseBursts()))
            using (var player = new SoundPlayer(ms))
            {
                player.Play();
                Pump(engine, 3.0);
            }
        }

        static Form ColorWindow(Color c)
        {
            var f = new Form
            {
                FormBorderStyle = FormBorderStyle.None,
                StartPosition = FormStartPosition.Manual,
                Bounds = Screen.PrimaryScreen.Bounds,
                TopMost = true,
                ShowInTaskbar = false,
                BackColor = c
            };
            return f;
        }

        static void Pump(Engine e, double seconds)
        {
            var until = DateTime.Now.AddSeconds(seconds);
            var nextPrint = DateTime.Now;
            while (DateTime.Now < until)
            {
                Application.DoEvents();
                if (DateTime.Now >= nextPrint)
                {
                    nextPrint = DateTime.Now.AddMilliseconds(250);
                    Log.Write(string.Format(CultureInfo.InvariantCulture,
                        "out={0} screen={1} vivid={2:0.00} peak={3:0.00} flash={4:0.00} cap={5}fps send={6}/s",
                        Settings.Hex(e.OutputRgb), Settings.Hex(e.ScreenRgb), e.Vivid, e.AudioPeak, e.Flash, e.CaptureFps, e.SendFps));
                }
                Thread.Sleep(10);
            }
            Log.Write(e.OpenRgbStatus + " | " + e.ScreenStatus + " | " + e.AudioStatus + " | " + e.KeyboardStatus);
        }

        /// <summary>Three short, moderate-volume noise bursts separated by silence (16-bit mono WAV).</summary>
        static byte[] NoiseBursts()
        {
            const int rate = 44100;
            int total = rate * 2;
            var samples = new short[total];
            var rnd = new Random(1);
            foreach (double t0 in new[] { 0.3, 0.9, 1.5 })
            {
                int s0 = (int)(t0 * rate), len = (int)(0.12 * rate);
                for (int i = 0; i < len; i++)
                {
                    double env = Math.Exp(-i / (0.04 * rate));
                    samples[s0 + i] = (short)((rnd.NextDouble() * 2 - 1) * 0.35 * env * short.MaxValue);
                }
            }
            var ms = new MemoryStream();
            var w = new BinaryWriter(ms);
            w.Write(new[] { 'R', 'I', 'F', 'F' }); w.Write(36 + total * 2);
            w.Write(new[] { 'W', 'A', 'V', 'E', 'f', 'm', 't', ' ' }); w.Write(16);
            w.Write((short)1); w.Write((short)1); w.Write(rate); w.Write(rate * 2); w.Write((short)2); w.Write((short)16);
            w.Write(new[] { 'd', 'a', 't', 'a' }); w.Write(total * 2);
            foreach (short s in samples) w.Write(s);
            w.Flush();
            return ms.ToArray();
        }
    }
#endif
}
