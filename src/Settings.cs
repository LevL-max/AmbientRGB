using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;

namespace AmbientRgb
{
    /// <summary>User settings. Fields are plain ints/bools so the engine thread can read them without locks.</summary>
    public sealed class Settings
    {
        public volatile int BaseColor = 0x00FF46;     // calm background color, 0xRRGGBB
        public volatile int FlashColor = 0xFFFFFF;    // color of audio flashes
        public volatile int IdleBrightness = 70;      // % brightness when nothing happens
        public volatile int ScreenInfluence = 85;     // % how strongly vivid screen colors replace the background
        public volatile int Smoothness = 35;          // % color transition smoothing
        public volatile int AudioSensitivity = 60;    // % 0 = audio flashes off
        public volatile int FlashStrength = 80;       // % how far a flash pushes toward FlashColor
        public volatile int MusicPulse;               // % brightness pulsing to the beat, 0 = off
        public volatile bool FullscreenOnly = true;   // follow the screen only while a fullscreen app (game, video) is in front
        public volatile bool Paused;                  // background color only
        public volatile bool KeyboardEnabled;         // AULA over cable only; off unless the user opts in

        public static string Dir
        {
            get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AmbientRGB"); }
        }

        static string FilePath { get { return Path.Combine(Dir, "settings.ini"); } }

        public static Settings Load()
        {
            var s = new Settings();
            try
            {
                if (!File.Exists(FilePath)) return s;
                foreach (string line in File.ReadAllLines(FilePath))
                {
                    int eq = line.IndexOf('=');
                    if (eq <= 0) continue;
                    s.Apply(line.Substring(0, eq).Trim(), line.Substring(eq + 1).Trim());
                }
            }
            catch (Exception ex) { Log.Write("Settings load failed: " + ex.Message); }
            return s;
        }

        string lastSaved;

        /// <summary>Saves only when something changed (the settings window calls this every second).</summary>
        public void SaveIfChanged()
        {
            if (string.Join("\n", Lines()) != lastSaved) Save();
        }

        public void Save()
        {
            try
            {
                Directory.CreateDirectory(Dir);
                string[] lines = Lines();
                File.WriteAllLines(FilePath, lines);
                lastSaved = string.Join("\n", lines);
            }
            catch (Exception ex) { Log.Write("Settings save failed: " + ex.Message); }
        }

        string[] Lines()
        {
            return new[]
            {
                "BaseColor=" + Hex(BaseColor),
                "FlashColor=" + Hex(FlashColor),
                "IdleBrightness=" + IdleBrightness,
                "ScreenInfluence=" + ScreenInfluence,
                "Smoothness=" + Smoothness,
                "AudioSensitivity=" + AudioSensitivity,
                "FlashStrength=" + FlashStrength,
                "MusicPulse=" + MusicPulse,
                "FullscreenOnly=" + FullscreenOnly,
                "Paused=" + Paused,
                "KeyboardEnabled=" + KeyboardEnabled,
            };
        }

        public void ResetToDefaults()
        {
            var d = new Settings();
            BaseColor = d.BaseColor;
            FlashColor = d.FlashColor;
            IdleBrightness = d.IdleBrightness;
            ScreenInfluence = d.ScreenInfluence;
            Smoothness = d.Smoothness;
            AudioSensitivity = d.AudioSensitivity;
            FlashStrength = d.FlashStrength;
            MusicPulse = d.MusicPulse;
            FullscreenOnly = d.FullscreenOnly;
            KeyboardEnabled = d.KeyboardEnabled;
        }

        void Apply(string key, string value)
        {
            switch (key)
            {
                case "BaseColor": BaseColor = ParseHex(value, BaseColor); break;
                case "FlashColor": FlashColor = ParseHex(value, FlashColor); break;
                case "IdleBrightness": IdleBrightness = ParsePercent(value, IdleBrightness); break;
                case "ScreenInfluence": ScreenInfluence = ParsePercent(value, ScreenInfluence); break;
                case "Smoothness": Smoothness = ParsePercent(value, Smoothness); break;
                case "AudioSensitivity": AudioSensitivity = ParsePercent(value, AudioSensitivity); break;
                case "FlashStrength": FlashStrength = ParsePercent(value, FlashStrength); break;
                case "MusicPulse": MusicPulse = ParsePercent(value, MusicPulse); break;
                case "FullscreenOnly": FullscreenOnly = !value.Equals("False", StringComparison.OrdinalIgnoreCase); break;
                case "Paused": Paused = value.Equals("True", StringComparison.OrdinalIgnoreCase); break;
                case "KeyboardEnabled": KeyboardEnabled = value.Equals("True", StringComparison.OrdinalIgnoreCase); break;
            }
        }

        public static string Hex(int rgb) { return "#" + (rgb & 0xFFFFFF).ToString("X6"); }

        static int ParseHex(string v, int fallback)
        {
            int r;
            return int.TryParse(v.TrimStart('#'), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out r) ? r & 0xFFFFFF : fallback;
        }

        static int ParsePercent(string v, int fallback)
        {
            int r;
            return int.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out r) ? Math.Max(0, Math.Min(100, r)) : fallback;
        }
    }

    public static class Log
    {
        static readonly object Gate = new object();
        static string path;
        public static bool Echo;

        public static void Init()
        {
            try
            {
                Directory.CreateDirectory(Settings.Dir);
                path = Path.Combine(Settings.Dir, "ambientrgb.log");
                if (File.Exists(path) && new FileInfo(path).Length > 512 * 1024) File.Delete(path);
            }
            catch { }
        }

        public static void Write(string msg)
        {
            string line = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture) + "  " + msg;
            lock (Gate)
            {
                if (Echo) Console.WriteLine(line);
                if (path == null) return;
                try { File.AppendAllText(path, line + Environment.NewLine); } catch { }
            }
        }
    }
}
