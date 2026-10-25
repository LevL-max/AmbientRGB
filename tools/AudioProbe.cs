using System;
using System.Diagnostics;
using System.Globalization;
using System.Threading;
using AmbientRgb;

// Reads the playback peak meter for N seconds and prints the same envelopes the engine uses. Touches no lights.
static class AudioProbe
{
    static void Main(string[] args)
    {
        int seconds = args.Length > 0 ? int.Parse(args[0]) : 8;
        using (var m = new AudioMeter())
        {
            Console.WriteLine("Device: " + m.DeviceId);
            var sw = Stopwatch.StartNew();
            double last = 0;
            float fast = 0, slow = 0, maxPeak = 0, maxRatio = 0;
            float avgS = 0, agc = 0.05f, pulse = 0, minF = 9, maxF = 0;
            int triggers = 0;
            double lastTrig = -1, nextBar = 0;
            while (sw.Elapsed.TotalSeconds < seconds)
            {
                double now = sw.Elapsed.TotalSeconds;
                float dt = (float)(now - last); last = now;
                float p = m.GetPeak();
                fast = p > fast ? p : fast * (float)Math.Exp(-dt / 0.06);
                slow += (fast - slow) * (1f - (float)Math.Exp(-dt / 1.5));
                float ratio = fast / (slow + 0.02f);
                maxPeak = Math.Max(maxPeak, p);
                maxRatio = Math.Max(maxRatio, ratio);
                // flash trigger as the engine does it at the default 60% sensitivity
                if (p >= 0.132f && ratio >= 2.09f && now - lastTrig > 0.07) { triggers++; lastTrig = now; }

                // music pulse at 50% depth
                avgS += (fast - avgS) * (1f - (float)Math.Exp(-dt / 0.4));
                agc = Math.Max(Math.Max(fast, 0.05f), agc * (float)Math.Exp(-dt / 4.0));
                float beat = Math.Max(0f, Math.Min(1f, (fast - avgS) / (agc * 0.3f + 0.01f)));
                pulse = Math.Max(beat, pulse * (float)Math.Exp(-dt / 0.15));
                float presence = Math.Min(1f, slow / 0.05f);
                float d = 0.5f;
                float F = 1f + presence * (-0.6f * d + d * pulse);
                if (now > 1.5) { minF = Math.Min(minF, F); maxF = Math.Max(maxF, F); }
                if (now >= nextBar && now > 1.5)
                {
                    nextBar = now + 0.1;
                    int n = (int)(F * 40);
                    Console.WriteLine(string.Format(CultureInfo.InvariantCulture, "{0,5:0.0}s F={1:0.00} ", now, F) + new string('#', Math.Max(0, n)));
                }
                Thread.Sleep(8);
            }
            Console.WriteLine(string.Format(CultureInfo.InvariantCulture,
                "MAX peak={0:0.00} MAX ratio={1:0.00} triggers(current settings)={2} pulseF min={3:0.00} max={4:0.00}", maxPeak, maxRatio, triggers, minF, maxF));
        }
    }
}
