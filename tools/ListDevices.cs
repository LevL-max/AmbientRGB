using System;
using System.Threading;
using AmbientRgb;

// Usage:
//   ListDevices                         list OpenRGB devices, zones and LEDs
//   ListDevices resize <dev> <zone> <n> set LED count of a resizable zone (ARGB header)
static class ListDevices
{
    static void Main(string[] args)
    {
        using (var c = new OpenRgbClient())
        {
            c.Connect("127.0.0.1", 6742, "AmbientRGB-tools");
            if (args.Length == 4 && args[0] == "resize")
            {
                c.ResizeZone(int.Parse(args[1]), int.Parse(args[2]), int.Parse(args[3]));
                Thread.Sleep(500);
                Console.WriteLine("Resized device " + args[1] + " zone " + args[2] + " to " + args[3]);
            }
            Console.WriteLine("Protocol: " + c.ProtocolVersion);
            foreach (var d in c.GetAllControllers())
            {
                Console.WriteLine(string.Format("[{0}] {1} | {2} | leds={3} | active={4}",
                    d.Index, d.TypeName, d.Name, d.LedCount,
                    d.ActiveMode >= 0 && d.ActiveMode < d.Modes.Count ? d.Modes[d.ActiveMode] : "?"));
                for (int zi = 0; zi < d.Zones.Count; zi++)
                {
                    var z = d.Zones[zi];
                    Console.WriteLine(string.Format("     zone {0} '{1}' type={2} leds={3}{4}", zi, z.Name, z.Type, z.LedsCount,
                        z.Matrix != null ? " matrix " + z.MatrixWidth + "x" + z.MatrixHeight : ""));
                }
                if (d.LedCount <= 12) Console.WriteLine("     LEDs: " + string.Join(", ", d.LedNames.ToArray()));
                if (d.Colors.Length > 0)
                {
                    uint col = d.Colors[0];
                    Console.WriteLine(string.Format("     current color (LED 0): R={0} G={1} B={2}", col & 0xFF, (col >> 8) & 0xFF, (col >> 16) & 0xFF));
                }
            }
        }
    }
}
