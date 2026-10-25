using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;

namespace AmbientRgb
{
    /// <summary>
    /// The lighting loop: background color, blended with the dominant vivid screen color,
    /// plus short flashes on sudden loud sounds. Runs on its own MTA thread.
    /// </summary>
    public sealed class Engine
    {
        readonly Settings s;
        Thread thread;
        volatile bool running;

        // Live state for the UI (written by the engine thread only).
        public volatile int OutputRgb;
        public volatile int ScreenRgb;
        public volatile float Vivid, AudioPeak, Flash;
        public volatile int CaptureFps, SendFps;
        public volatile bool ScreenActive;
        public volatile string OpenRgbStatus = "OpenRGB: connecting…";
        public volatile string ScreenStatus = "Screen: starting…";
        public volatile string AudioStatus = "Audio: starting…";
        public volatile string KeyboardStatus = "Keyboard: searching…";

        sealed class Target { public int Index; public uint[] Buffer; }

        OpenRgbClient orgb;
        readonly List<Target> targets = new List<Target>();
        double nextOrgbTry, nextNotifPoll;
        bool forceSend;

        ScreenCapture cap;
        double nextCapTry, nextFullscreenCheck;
        bool fullscreen;
        string lastCapError;
        float tgtR = 1, tgtG = 1, tgtB = 1, tgtVivid;
        float scrR = 1, scrG = 1, scrB = 1, scrVivid;

        AudioMeter audio;
        double nextAudioTry, nextAudioCheck;
        float fastEnv, slowEnv, flash;
        float beatAvg, beatAgc = 0.05f, pulse;
        double lastTrigger = -10;

        AulaKeyboard kb;
        double nextKbTry;

        public Engine(Settings settings) { s = settings; }

        public void Start()
        {
            running = true;
            thread = new Thread(Run);
            thread.IsBackground = true;
            thread.Name = "AmbientRGB engine";
            thread.SetApartmentState(ApartmentState.MTA);
            thread.Start();
        }

        public void Stop()
        {
            running = false;
            if (thread != null && !thread.Join(3000)) Log.Write("Engine thread did not stop in time");
        }

        void Run()
        {
            timeBeginPeriod(1);
            try { Loop(); }
            catch (Exception ex) { Log.Write("Engine crashed: " + ex); }
            finally
            {
                try { SendToDevices(CalmColor()); } catch { }
                if (orgb != null) orgb.Dispose();
                if (cap != null) cap.Dispose();
                if (audio != null) audio.Dispose();
                if (kb != null) kb.Dispose();
                timeEndPeriod(1);
            }
        }

        void Loop()
        {
            var sw = Stopwatch.StartNew();
            double last = 0, nextScreen = 0, nextSend = 0, lastSent = -10, statT = 0;
            int lastSentRgb = -1, frames = 0, sends = 0;

            while (running)
            {
                double now = sw.Elapsed.TotalSeconds;
                float dt = (float)Math.Min(0.1, Math.Max(0.0, now - last));
                last = now;
                bool paused = s.Paused;

                UpdateAudio(now, dt, paused);

                if (now >= nextFullscreenCheck)
                {
                    nextFullscreenCheck = now + 0.25;
                    fullscreen = IsFullscreenAppInFront();
                }
                bool screenOn = !paused && s.ScreenInfluence > 0 && (fullscreen || !s.FullscreenOnly);
                if (!screenOn) tgtVivid = 0f;
                ScreenActive = screenOn;

                if (now >= nextScreen)
                {
                    nextScreen = now + 1.0 / 30;
                    if (screenOn && UpdateScreen(now)) frames++;
                }

                float tau = Lerp(0.04f, 0.7f, s.Smoothness / 100f);
                float k = 1f - (float)Math.Exp(-dt / tau);
                float vividTarget = (!screenOn || cap == null) ? 0f : tgtVivid;
                scrR += (tgtR - scrR) * k;
                scrG += (tgtG - scrG) * k;
                scrB += (tgtB - scrB) * k;
                scrVivid += (vividTarget - scrVivid) * k;

                ServiceOpenRgb(now);
                ServiceKeyboard(now);

                if (now >= nextSend)
                {
                    nextSend = now + 1.0 / 40;
                    int rgb = ComposeColor(paused);
                    OutputRgb = rgb;
                    if (rgb != lastSentRgb || forceSend || now - lastSent > 2.0)
                    {
                        forceSend = false;
                        if (SendToDevices(rgb)) { lastSentRgb = rgb; sends++; }
                        lastSent = now;
                    }
                }

                if (now - statT >= 1.0)
                {
                    CaptureFps = frames;
                    SendFps = sends;
                    frames = 0;
                    sends = 0;
                    statT = now;
                }

                Thread.Sleep(8);
            }
        }

        // ---------------- color ----------------

        /// <summary>
        /// Mixes in linear light: picker and screen colors are sRGB, LED PWM is linear. Without this a
        /// "green" with a little blue looks teal on the LEDs. Returns LED (linear) values.
        /// </summary>
        int ComposeColor(bool paused)
        {
            float r = Lin(R(s.BaseColor)), g = Lin(G(s.BaseColor)), b = Lin(B(s.BaseColor));
            if (!paused)
            {
                float w = Clamp01(VividWeight(scrVivid) * s.ScreenInfluence / 100f);
                r = Lerp(r, Lin(scrR), w);
                g = Lerp(g, Lin(scrG), w);
                b = Lerp(b, Lin(scrB), w);
            }
            float level = s.IdleBrightness / 100f;
            if (!paused) level *= PulseFactor();
            r *= level; g *= level; b *= level;
            if (!paused)
            {
                float f = Clamp01(flash * s.FlashStrength / 100f);
                r = Lerp(r, Lin(R(s.FlashColor)), f);
                g = Lerp(g, Lin(G(s.FlashColor)), f);
                b = Lerp(b, Lin(B(s.FlashColor)), f);
            }
            return ToRgb(r, g, b);
        }

        int CalmColor() { return ComposeColor(true); }

        /// <summary>Small colored areas (desktop UI, wallpaper accents) barely count; colorful scenes count fully.</summary>
        static float VividWeight(float vivid) { return Clamp01((vivid - 0.2f) / 0.6f); }

        static float Lin(float v) { return (float)Math.Pow(v, 2.2); }

        // ---------------- audio ----------------

        void UpdateAudio(double now, float dt, bool paused)
        {
            if (audio == null)
            {
                if (now >= nextAudioTry)
                {
                    nextAudioTry = now + 3;
                    try
                    {
                        audio = new AudioMeter();
                        AudioStatus = "Audio: OK";
                        nextAudioCheck = now + 2;
                        Log.Write("Audio meter opened: " + audio.DeviceId);
                    }
                    catch (Exception ex) { AudioStatus = "Audio: no playback device"; Log.Write("Audio meter: " + ex.Message); }
                }
            }
            else if (now >= nextAudioCheck)
            {
                nextAudioCheck = now + 2;
                try { audio.CheckDefaultDevice(); }
                catch (Exception ex) { DropAudio(ex); }
            }

            float peak = 0f;
            if (audio != null)
            {
                try { peak = audio.GetPeak(); }
                catch (Exception ex) { DropAudio(ex); }
            }
            AudioPeak = peak;

            fastEnv = peak > fastEnv ? peak : fastEnv * (float)Math.Exp(-dt / 0.06);
            slowEnv += (fastEnv - slowEnv) * (1f - (float)Math.Exp(-dt / 1.5));

            float sens = s.AudioSensitivity / 100f;
            if (!paused && sens > 0f)
            {
                float ratioThreshold = Lerp(3.2f, 1.35f, sens);
                float minLevel = Lerp(0.30f, 0.02f, sens);
                float ratio = fastEnv / (slowEnv + 0.02f);
                if (peak >= minLevel && ratio >= ratioThreshold && now - lastTrigger > 0.07)
                {
                    float strength = Clamp01(0.45f + (ratio - ratioThreshold) / ratioThreshold) * Clamp01(0.4f + peak);
                    if (strength > flash) flash = strength;
                    lastTrigger = now;
                }
            }
            flash *= (float)Math.Exp(-dt / 0.16);
            if (flash < 0.003f) flash = 0f;
            Flash = flash;

            // Beat pulse: how far the level rises above its ~0.4 s average, relative to the recent loudest level.
            beatAvg += (fastEnv - beatAvg) * (1f - (float)Math.Exp(-dt / 0.4));
            beatAgc = Math.Max(Math.Max(fastEnv, 0.05f), beatAgc * (float)Math.Exp(-dt / 4.0));
            float beat = Clamp01((fastEnv - beatAvg) / (beatAgc * 0.3f + 0.01f));
            pulse = Math.Max(beat, pulse * (float)Math.Exp(-dt / 0.15));
        }

        /// <summary>Brightness multiplier for the music pulse: dims between beats, lifts on beats; 1 when silent.</summary>
        float PulseFactor()
        {
            float depth = s.MusicPulse / 100f;
            if (depth <= 0f) return 1f;
            float presence = Clamp01(slowEnv / 0.05f);
            return 1f + presence * depth * (pulse - 0.6f);
        }

        void DropAudio(Exception ex)
        {
            Log.Write("Audio meter lost: " + ex.Message);
            if (audio != null) audio.Dispose();
            audio = null;
            AudioStatus = "Audio: reconnecting…";
        }

        // ---------------- screen ----------------

        bool UpdateScreen(double now)
        {
            if (cap == null)
            {
                if (now < nextCapTry) return false;
                nextCapTry = now + 1.5;
                try
                {
                    cap = new ScreenCapture();
                    ScreenStatus = "Screen: " + cap.OutputName;
                    Log.Write("Screen capture started: " + cap.OutputName);
                    lastCapError = null;
                }
                catch (Exception ex) { cap = null; ReportCaptureError(ex); return false; }
            }
            try
            {
                if (!cap.TryCapture()) return false;
                float r, g, b, v;
                if (ColorAnalysis.Analyze(cap.Pixels, cap.Width, cap.Height, out r, out g, out b, out v))
                {
                    tgtR = r; tgtG = g; tgtB = b;
                }
                tgtVivid = v;
                ScreenRgb = ToRgb(tgtR, tgtG, tgtB);
                Vivid = v;
                return true;
            }
            catch (Exception ex)
            {
                cap.Dispose();
                cap = null;
                tgtVivid = 0f;
                nextCapTry = now + 0.5;
                ReportCaptureError(ex);
                return false;
            }
        }

        void ReportCaptureError(Exception ex)
        {
            ScreenStatus = "Screen: waiting for capture";
            if (ex.Message != lastCapError) Log.Write("Screen capture: " + ex.Message);
            lastCapError = ex.Message;
        }

        // ---------------- devices ----------------

        void ServiceOpenRgb(double now)
        {
            if (orgb == null)
            {
                if (now < nextOrgbTry) return;
                nextOrgbTry = now + 3;
                var c = new OpenRgbClient();
                try
                {
                    c.Connect("127.0.0.1", 6742, "AmbientRGB");
                    LoadDevices(c);
                    orgb = c;
                    nextNotifPoll = now + 1;
                }
                catch (Exception ex)
                {
                    c.Dispose();
                    string msg = "OpenRGB: server not reachable (is the OpenRGB service running?)";
                    if (OpenRgbStatus != msg) Log.Write("OpenRGB connect failed: " + ex.Message);
                    OpenRgbStatus = msg;
                }
                return;
            }
            if (now >= nextNotifPoll)
            {
                nextNotifPoll = now + 1;
                try
                {
                    orgb.PollNotifications();
                    if (orgb.DeviceListChanged)
                    {
                        orgb.DeviceListChanged = false;
                        Log.Write("OpenRGB device list changed, reloading");
                        LoadDevices(orgb);
                    }
                }
                catch (Exception ex) { DropOpenRgb(ex); }
            }
        }

        void LoadDevices(OpenRgbClient c)
        {
            targets.Clear();
            var names = new List<string>();
            foreach (var d in c.GetAllControllers())
            {
                if (d.LedCount == 0) continue;
                c.SetCustomMode(d.Index);
                targets.Add(new Target { Index = d.Index, Buffer = new uint[d.LedCount] });
                names.Add(d.TypeName);
                Log.Write("OpenRGB device [" + d.Index + "] " + d.TypeName + ": " + d.Name + " (" + d.LedCount + " LEDs)");
            }
            OpenRgbStatus = "OpenRGB: " + targets.Count + " devices (" + string.Join(", ", names.ToArray()) + ")";
            forceSend = true;
        }

        void DropOpenRgb(Exception ex)
        {
            Log.Write("OpenRGB connection lost: " + ex.Message);
            if (orgb != null) orgb.Dispose();
            orgb = null;
            targets.Clear();
            OpenRgbStatus = "OpenRGB: reconnecting…";
        }

        void ServiceKeyboard(double now)
        {
            if (!s.KeyboardEnabled)
            {
                if (kb != null) { kb.Dispose(); kb = null; }
                KeyboardStatus = "Keyboard: off";
                return;
            }
            if (kb != null || now < nextKbTry) return;
            nextKbTry = now + 5;
            string status;
            kb = AulaKeyboard.TryOpen(out status);
            KeyboardStatus = status;
            if (kb != null) forceSend = true;
        }

        bool SendToDevices(int rgb)
        {
            int r = (rgb >> 16) & 0xFF, g = (rgb >> 8) & 0xFF, b = rgb & 0xFF;
            bool any = false;
            if (orgb != null)
            {
                uint c = OpenRgbClient.Rgb(r, g, b);
                try
                {
                    foreach (var t in targets)
                    {
                        for (int i = 0; i < t.Buffer.Length; i++) t.Buffer[i] = c;
                        orgb.UpdateLeds(t.Index, t.Buffer);
                    }
                    any = targets.Count > 0;
                }
                catch (Exception ex) { DropOpenRgb(ex); }
            }
            if (kb != null)
            {
                try { kb.SetColor((byte)r, (byte)g, (byte)b); any = true; }
                catch (Exception ex)
                {
                    Log.Write("Keyboard write failed: " + ex.Message);
                    kb.Dispose();
                    kb = null;
                    KeyboardStatus = "Keyboard: connection lost";
                }
            }
            return any;
        }

        // ---------------- helpers ----------------

        static float R(int rgb) { return ((rgb >> 16) & 0xFF) / 255f; }
        static float G(int rgb) { return ((rgb >> 8) & 0xFF) / 255f; }
        static float B(int rgb) { return (rgb & 0xFF) / 255f; }
        static float Lerp(float a, float b, float t) { return a + (b - a) * t; }
        static float Clamp01(float v) { return v < 0f ? 0f : (v > 1f ? 1f : v); }

        public static int ToRgb(float r, float g, float b)
        {
            int ri = (int)Math.Round(Clamp01(r) * 255f), gi = (int)Math.Round(Clamp01(g) * 255f), bi = (int)Math.Round(Clamp01(b) * 255f);
            return (ri << 16) | (gi << 8) | bi;
        }

        /// <summary>
        /// True when the foreground window covers the whole primary monitor: games (exclusive or borderless)
        /// and fullscreen video. Maximized windows don't count (the taskbar stays visible), nor does the desktop.
        /// </summary>
        static bool IsFullscreenAppInFront()
        {
            IntPtr hwnd = GetForegroundWindow();
            if (hwnd == IntPtr.Zero) return false;
            var cls = new System.Text.StringBuilder(64);
            GetClassName(hwnd, cls, cls.Capacity);
            string c = cls.ToString();
            if (c == "Progman" || c == "WorkerW" || c == "Shell_TrayWnd") return false;

            RECT wr;
            if (!GetWindowRect(hwnd, out wr)) return false;
            var mi = new MONITORINFO();
            mi.cbSize = Marshal.SizeOf(typeof(MONITORINFO));
            if (!GetMonitorInfo(MonitorFromWindow(hwnd, 2 /* NEAREST */), ref mi)) return false;
            if ((mi.dwFlags & 1) == 0) return false; // only the primary monitor is captured
            bool covers = wr.Left <= mi.rcMonitor.Left && wr.Top <= mi.rcMonitor.Top
                && wr.Right >= mi.rcMonitor.Right && wr.Bottom >= mi.rcMonitor.Bottom;
            if (!covers) return false;
            // With an auto-hidden taskbar a maximized normal window also covers the monitor; it has a title bar.
            const int WS_CAPTION = 0x00C00000;
            bool hasCaption = (GetWindowLong(hwnd, -16 /* GWL_STYLE */) & WS_CAPTION) == WS_CAPTION;
            return !(IsZoomed(hwnd) && hasCaption);
        }

        [StructLayout(LayoutKind.Sequential)]
        struct RECT { public int Left, Top, Right, Bottom; }

        [StructLayout(LayoutKind.Sequential)]
        struct MONITORINFO { public int cbSize; public RECT rcMonitor; public RECT rcWork; public uint dwFlags; }

        [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetClassName(IntPtr hwnd, System.Text.StringBuilder name, int max);
        [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);
        [DllImport("user32.dll")] static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);
        [DllImport("user32.dll")] static extern bool GetMonitorInfo(IntPtr monitor, ref MONITORINFO info);
        [DllImport("user32.dll")] static extern int GetWindowLong(IntPtr hwnd, int index);
        [DllImport("user32.dll")] static extern bool IsZoomed(IntPtr hwnd);

        [DllImport("winmm.dll")] static extern uint timeBeginPeriod(uint ms);
        [DllImport("winmm.dll")] static extern uint timeEndPeriod(uint ms);
    }

    public static class ColorAnalysis
    {
        const int Bins = 24; // 15 degrees of hue each

        /// <summary>
        /// Finds the dominant vivid hue of a BGRA image. Gray and dark pixels are ignored, so a dull
        /// scene yields vivid≈0 and the lights stay on the background color.
        /// </summary>
        public static bool Analyze(byte[] px, int w, int h, out float r, out float g, out float b, out float vivid)
        {
            var bw = new float[Bins];
            var br = new float[Bins];
            var bg = new float[Bins];
            var bb = new float[Bins];
            int n = w * h;

            for (int i = 0; i < n; i++)
            {
                int B = px[i * 4], G = px[i * 4 + 1], R = px[i * 4 + 2];
                int max = Math.Max(R, Math.Max(G, B));
                int min = Math.Min(R, Math.Min(G, B));
                if (max < 24) continue;
                int d = max - min;
                float sat = d / (float)max;
                if (sat < 0.15f) continue;
                float v = max / 255f;
                float weight = sat * sat * v;

                float hue;
                if (max == R) hue = (G - B) / (float)d;
                else if (max == G) hue = 2f + (B - R) / (float)d;
                else hue = 4f + (R - G) / (float)d;
                hue *= 60f;
                if (hue < 0f) hue += 360f;
                int bin = ((int)(hue / (360f / Bins))) % Bins;

                bw[bin] += weight;
                br[bin] += R * weight;
                bg[bin] += G * weight;
                bb[bin] += B * weight;
            }

            int best = -1;
            float bestW = 0f;
            for (int i = 0; i < Bins; i++)
            {
                float sum = bw[(i + Bins - 1) % Bins] + bw[i] + bw[(i + 1) % Bins];
                if (sum > bestW) { bestW = sum; best = i; }
            }

            r = g = b = 0f;
            vivid = 0f;
            if (best < 0 || bestW <= 0f) return false;

            float sr = 0, sg = 0, sb = 0;
            for (int j = -1; j <= 1; j++)
            {
                int i = (best + j + Bins) % Bins;
                sr += br[i]; sg += bg[i]; sb += bb[i];
            }
            sr /= bestW; sg /= bestW; sb /= bestW;

            float m = Math.Max(sr, Math.Max(sg, sb));
            if (m <= 0f) return false;
            r = sr / m; g = sg / m; b = sb / m;

            // A bit more saturation reads better on LEDs.
            float gray = (r + g + b) / 3f;
            r = Math.Max(0f, Math.Min(1f, gray + (r - gray) * 1.25f));
            g = Math.Max(0f, Math.Min(1f, gray + (g - gray) * 1.25f));
            b = Math.Max(0f, Math.Min(1f, gray + (b - gray) * 1.25f));

            vivid = (float)Math.Min(1.0, Math.Sqrt(bestW / n) * 1.6);
            return true;
        }
    }
}
