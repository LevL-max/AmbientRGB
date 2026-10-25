using System;
using System.Globalization;
using System.Threading;
using AmbientRgb;

// Captures the screen for a few seconds and prints what the engine would see. Touches no lights.
static class ScreenProbe
{
    static void Main()
    {
        using (var cap = new ScreenCapture())
        {
            Console.WriteLine("Output: " + cap.OutputName);
            int got = 0;
            for (int i = 0; i < 60 && got < 6; i++)
            {
                Thread.Sleep(100);
                if (!cap.TryCapture()) continue;
                got++;
                float r, g, b, v;
                bool ok = ColorAnalysis.Analyze(cap.Pixels, cap.Width, cap.Height, out r, out g, out b, out v);

                // Share of pixels by rough category, to explain where the color comes from.
                int n = cap.Width * cap.Height, dark = 0, gray = 0, blue = 0, other = 0;
                for (int k = 0; k < n; k++)
                {
                    int B = cap.Pixels[k * 4], G = cap.Pixels[k * 4 + 1], R = cap.Pixels[k * 4 + 2];
                    int max = Math.Max(R, Math.Max(G, B)), min = Math.Min(R, Math.Min(G, B));
                    if (max < 24) dark++;
                    else if ((max - min) / (float)max < 0.15f) gray++;
                    else if (B == max) blue++;
                    else other++;
                }
                Console.WriteLine(string.Format(CultureInfo.InvariantCulture,
                    "screen color={0} vivid={1:0.00} | dark {2}% gray {3}% blue-ish {4}% other colored {5}%",
                    ok ? Settings.Hex(Engine.ToRgb(r, g, b)) : "none", v,
                    dark * 100 / n, gray * 100 / n, blue * 100 / n, other * 100 / n));
            }
            if (got == 0) Console.WriteLine("No new frames (screen static) — move the mouse over a window or wait.");
        }
    }
}
