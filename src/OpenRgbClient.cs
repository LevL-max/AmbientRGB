using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Sockets;
using System.Text;

namespace AmbientRgb
{
    public sealed class OrgbZone
    {
        public string Name = "";
        public int Type;          // 0 single, 1 linear, 2 matrix
        public int LedsCount;
        public int MatrixWidth, MatrixHeight;
        public uint[] Matrix;     // LED index per cell, 0xFFFFFFFF = empty
    }

    public sealed class OrgbDevice
    {
        public int Index;
        public int Type;
        public string Name = "", Vendor = "", Description = "", Version = "", Serial = "", Location = "";
        public List<string> Modes = new List<string>();
        public int ActiveMode;
        public List<OrgbZone> Zones = new List<OrgbZone>();
        public List<string> LedNames = new List<string>();
        public uint[] Colors = new uint[0];

        public int LedCount { get { return LedNames.Count; } }
        public string TypeName { get { return OpenRgbClient.DeviceTypeName(Type); } }
        public bool HasDirectMode { get { return Modes.Exists(m => m.Equals("Direct", StringComparison.OrdinalIgnoreCase)); } }
    }

    /// <summary>Minimal client for the OpenRGB SDK network protocol (default port 6742).</summary>
    public sealed class OpenRgbClient : IDisposable
    {
        const uint REQUEST_CONTROLLER_COUNT = 0;
        const uint REQUEST_CONTROLLER_DATA = 1;
        const uint REQUEST_PROTOCOL_VERSION = 40;
        const uint SET_CLIENT_NAME = 50;
        const uint DEVICE_LIST_UPDATED = 100;
        const uint RGBCONTROLLER_RESIZEZONE = 1000;
        const uint RGBCONTROLLER_UPDATELEDS = 1050;
        const uint RGBCONTROLLER_SETCUSTOMMODE = 1100;

        // Protocol 3 is a stable, well-known layout; newer servers serialize for the client's version.
        public const uint ClientProtocolVersion = 3;

        TcpClient tcp;
        NetworkStream ns;

        public uint ProtocolVersion { get; private set; }
        public bool DeviceListChanged { get; set; }
        public bool Connected { get { return tcp != null && tcp.Connected; } }

        public void Connect(string host, int port, string clientName)
        {
            tcp = new TcpClient();
            tcp.NoDelay = true;
            IAsyncResult ar = tcp.BeginConnect(host, port, null, null);
            if (!ar.AsyncWaitHandle.WaitOne(1500))
            {
                tcp.Close();
                tcp = null;
                throw new IOException("OpenRGB SDK server is not reachable on " + host + ":" + port);
            }
            tcp.EndConnect(ar);
            tcp.ReceiveTimeout = 3000;
            tcp.SendTimeout = 3000;
            ns = tcp.GetStream();

            Send(0, REQUEST_PROTOCOL_VERSION, BitConverter.GetBytes(ClientProtocolVersion));
            byte[] reply = ReadReply(REQUEST_PROTOCOL_VERSION);
            uint server = reply.Length >= 4 ? BitConverter.ToUInt32(reply, 0) : 0;
            ProtocolVersion = Math.Min(server, ClientProtocolVersion);

            Send(0, SET_CLIENT_NAME, Encoding.ASCII.GetBytes(clientName + "\0"));
        }

        public int GetControllerCount()
        {
            Send(0, REQUEST_CONTROLLER_COUNT, null);
            return (int)BitConverter.ToUInt32(ReadReply(REQUEST_CONTROLLER_COUNT), 0);
        }

        public OrgbDevice GetController(int index)
        {
            byte[] payload = ProtocolVersion >= 1 ? BitConverter.GetBytes(ProtocolVersion) : null;
            Send((uint)index, REQUEST_CONTROLLER_DATA, payload);
            return ParseController(index, ReadReply(REQUEST_CONTROLLER_DATA), ProtocolVersion);
        }

        public List<OrgbDevice> GetAllControllers()
        {
            var list = new List<OrgbDevice>();
            int count = GetControllerCount();
            for (int i = 0; i < count; i++) list.Add(GetController(i));
            return list;
        }

        /// <summary>Sets the LED count of a resizable zone (e.g. an ARGB header). OpenRGB stores it in its size profile.</summary>
        public void ResizeZone(int index, int zone, int size)
        {
            byte[] p = new byte[8];
            PutU32(p, 0, (uint)zone);
            PutU32(p, 4, (uint)size);
            Send((uint)index, RGBCONTROLLER_RESIZEZONE, p);
        }

        /// <summary>Switches the device to Direct (or Custom/Static as fallback) mode.</summary>
        public void SetCustomMode(int index)
        {
            Send((uint)index, RGBCONTROLLER_SETCUSTOMMODE, null);
        }

        public void UpdateLeds(int index, uint[] colors)
        {
            int n = colors.Length;
            uint size = (uint)(4 + 2 + 4 * n);
            byte[] p = new byte[size];
            PutU32(p, 0, size);
            p[4] = (byte)(n & 0xFF);
            p[5] = (byte)((n >> 8) & 0xFF);
            for (int i = 0; i < n; i++) PutU32(p, 6 + 4 * i, colors[i]);
            Send((uint)index, RGBCONTROLLER_UPDATELEDS, p);
        }

        /// <summary>Drains asynchronous server notifications without blocking.</summary>
        public void PollNotifications()
        {
            while (ns != null && ns.DataAvailable)
            {
                uint id;
                ReadPacket(out id);
                if (id == DEVICE_LIST_UPDATED) DeviceListChanged = true;
            }
        }

        public static uint Rgb(int r, int g, int b)
        {
            return (uint)((r & 0xFF) | ((g & 0xFF) << 8) | ((b & 0xFF) << 16));
        }

        public static string DeviceTypeName(int type)
        {
            switch (type)
            {
                case 0: return "Motherboard";
                case 1: return "DRAM";
                case 2: return "GPU";
                case 3: return "Cooler";
                case 4: return "LED Strip";
                case 5: return "Keyboard";
                case 6: return "Mouse";
                case 7: return "Mousemat";
                case 8: return "Headset";
                case 9: return "Headset Stand";
                case 10: return "Gamepad";
                case 11: return "Light";
                case 12: return "Speaker";
                case 13: return "Virtual";
                case 14: return "Storage";
                case 15: return "Case";
                case 16: return "Microphone";
                case 17: return "Accessory";
                case 18: return "Keypad";
                default: return "Type " + type;
            }
        }

        public void Dispose()
        {
            try { if (ns != null) ns.Dispose(); } catch { }
            try { if (tcp != null) tcp.Close(); } catch { }
            ns = null;
            tcp = null;
        }

        // ---- wire helpers ----

        void Send(uint devIdx, uint pktId, byte[] payload)
        {
            int len = payload == null ? 0 : payload.Length;
            byte[] buf = new byte[16 + len];
            buf[0] = (byte)'O'; buf[1] = (byte)'R'; buf[2] = (byte)'G'; buf[3] = (byte)'B';
            PutU32(buf, 4, devIdx);
            PutU32(buf, 8, pktId);
            PutU32(buf, 12, (uint)len);
            if (len > 0) Buffer.BlockCopy(payload, 0, buf, 16, len);
            ns.Write(buf, 0, buf.Length);
        }

        byte[] ReadReply(uint wantedId)
        {
            while (true)
            {
                uint id;
                byte[] data = ReadPacket(out id);
                if (id == wantedId) return data;
                if (id == DEVICE_LIST_UPDATED) DeviceListChanged = true;
            }
        }

        byte[] ReadPacket(out uint pktId)
        {
            byte[] hdr = ReadExact(16);
            if (hdr[0] != 'O' || hdr[1] != 'R' || hdr[2] != 'G' || hdr[3] != 'B')
                throw new IOException("Bad OpenRGB packet magic");
            pktId = BitConverter.ToUInt32(hdr, 8);
            uint size = BitConverter.ToUInt32(hdr, 12);
            return size > 0 ? ReadExact((int)size) : new byte[0];
        }

        byte[] ReadExact(int n)
        {
            byte[] buf = new byte[n];
            int off = 0;
            while (off < n)
            {
                int r = ns.Read(buf, off, n - off);
                if (r <= 0) throw new IOException("OpenRGB connection closed");
                off += r;
            }
            return buf;
        }

        static void PutU32(byte[] b, int off, uint v)
        {
            b[off] = (byte)v;
            b[off + 1] = (byte)(v >> 8);
            b[off + 2] = (byte)(v >> 16);
            b[off + 3] = (byte)(v >> 24);
        }

        static OrgbDevice ParseController(int index, byte[] d, uint proto)
        {
            var r = new Reader(d);
            var dev = new OrgbDevice();
            dev.Index = index;
            r.U32(); // data size
            dev.Type = r.I32();
            dev.Name = r.Str();
            if (proto >= 1) dev.Vendor = r.Str();
            dev.Description = r.Str();
            dev.Version = r.Str();
            dev.Serial = r.Str();
            dev.Location = r.Str();

            int numModes = r.U16();
            dev.ActiveMode = r.I32();
            for (int i = 0; i < numModes; i++)
            {
                string name = r.Str();
                r.I32();                          // value
                r.U32();                          // flags
                r.U32(); r.U32();                 // speed min/max
                if (proto >= 3) { r.U32(); r.U32(); } // brightness min/max
                r.U32(); r.U32();                 // colors min/max
                r.U32();                          // speed
                if (proto >= 3) r.U32();          // brightness
                r.U32();                          // direction
                r.U32();                          // color mode
                r.Skip(r.U16() * 4);              // mode colors
                dev.Modes.Add(name);
            }

            int numZones = r.U16();
            for (int i = 0; i < numZones; i++)
            {
                var z = new OrgbZone();
                z.Name = r.Str();
                z.Type = r.I32();
                r.U32(); r.U32();                 // leds min/max
                z.LedsCount = (int)r.U32();
                int matrixLen = r.U16();
                if (matrixLen > 0)
                {
                    z.MatrixHeight = (int)r.U32();
                    z.MatrixWidth = (int)r.U32();
                    z.Matrix = new uint[z.MatrixHeight * z.MatrixWidth];
                    for (int k = 0; k < z.Matrix.Length; k++) z.Matrix[k] = r.U32();
                }
                if (proto >= 4)
                {
                    int segs = r.U16();
                    for (int s = 0; s < segs; s++) { r.Str(); r.I32(); r.U32(); r.U32(); }
                }
                dev.Zones.Add(z);
            }

            int numLeds = r.U16();
            for (int i = 0; i < numLeds; i++) { dev.LedNames.Add(r.Str()); r.U32(); }

            int numColors = r.U16();
            dev.Colors = new uint[numColors];
            for (int i = 0; i < numColors; i++) dev.Colors[i] = r.U32();
            return dev;
        }

        sealed class Reader
        {
            readonly byte[] d;
            int p;
            public Reader(byte[] data) { d = data; }
            public uint U32() { uint v = BitConverter.ToUInt32(d, p); p += 4; return v; }
            public int I32() { int v = BitConverter.ToInt32(d, p); p += 4; return v; }
            public int U16() { int v = BitConverter.ToUInt16(d, p); p += 2; return v; }
            public void Skip(int n) { p += n; }
            public string Str()
            {
                int len = U16();
                string s = len > 0 ? Encoding.UTF8.GetString(d, p, len).TrimEnd('\0') : "";
                p += len;
                return s;
            }
        }
    }
}
