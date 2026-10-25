using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace AmbientRgb
{
    /// <summary>
    /// AULA Hero HE keyboards (BY Tech controller, VID 0x372E). Lighting is controlled over the
    /// vendor HID collection (usage page 0xFF60 / usage 0x61) and only works over the USB cable;
    /// the 2.4G receiver firmware does not forward lighting commands.
    /// </summary>
    public sealed class AulaKeyboard : IDisposable
    {
        const ushort VendorId = 0x372E;
        const ushort UsagePage = 0xFF60;
        const ushort Usage = 0x61;

        // LED indexes used by the Hero 68/84 HE firmware family (1..77 plus the nav column 95..103).
        static readonly byte[] LedIndexes = BuildLedIndexes();

        SafeFileHandle handle;
        FileStream stream;
        int reportLength;

        public string Product { get; private set; }

        static byte[] BuildLedIndexes()
        {
            var list = new List<byte>();
            for (int i = 1; i <= 77; i++) list.Add((byte)i);
            for (int i = 95; i <= 103; i++) list.Add((byte)i);
            return list.ToArray();
        }

        /// <summary>Finds a cable-connected AULA keyboard. Returns null and a human-readable status otherwise.</summary>
        public static AulaKeyboard TryOpen(out string status)
        {
            bool sawReceiver = false;
            foreach (string path in EnumerateHidPaths())
            {
                if (path.IndexOf("vid_372e", StringComparison.OrdinalIgnoreCase) < 0) continue;
                string product;
                int outLen;
                if (!QueryDevice(path, out product, out outLen)) continue;

                string p = product.ToLowerInvariant();
                if (p.Contains("receiver") || p.Contains("dongle")) { sawReceiver = true; continue; }

                var kb = new AulaKeyboard();
                try
                {
                    kb.Open(path, outLen);
                    kb.Product = product;
                    status = "Keyboard: " + product + " (USB cable) — connected";
                    Log.Write("AULA keyboard opened: '" + product + "' report=" + outLen + " path=" + path);
                    return kb;
                }
                catch (Exception ex)
                {
                    kb.Dispose();
                    Log.Write("AULA keyboard open failed: " + ex.Message);
                }
            }
            status = sawReceiver
                ? "Keyboard: AULA on the 2.4G receiver — lighting works over USB cable only"
                : "Keyboard: no AULA keyboard on USB cable";
            return null;
        }

        void Open(string path, int outLen)
        {
            handle = CreateFile(path, GENERIC_READ | GENERIC_WRITE, FILE_SHARE_READ | FILE_SHARE_WRITE,
                IntPtr.Zero, OPEN_EXISTING, FILE_FLAG_OVERLAPPED, IntPtr.Zero);
            if (handle.IsInvalid) throw new IOException("CreateFile failed, error " + Marshal.GetLastWin32Error());
            reportLength = Math.Max(64, outLen);
            stream = new FileStream(handle, FileAccess.ReadWrite, reportLength, true);
        }

        /// <summary>Sets every key to one color (one color group: R, G, B, count, indexes...).</summary>
        public void SetColor(byte r, byte g, byte b)
        {
            var data = new List<byte>(4 + LedIndexes.Length);
            data.Add(r); data.Add(g); data.Add(b); data.Add((byte)LedIndexes.Length);
            data.AddRange(LedIndexes);

            const int chunk = 56;
            int total = (data.Count + chunk - 1) / chunk;
            for (int i = 0; i < total; i++)
            {
                int len = Math.Min(chunk, data.Count - i * chunk);
                byte[] pkt = new byte[reportLength];
                pkt[0] = 0x09; pkt[1] = 0x08; pkt[2] = 0x01; pkt[3] = 0x00;
                pkt[4] = (byte)total; pkt[5] = (byte)i; pkt[6] = (byte)len;
                data.CopyTo(i * chunk, pkt, 7, len);
                int sum = 0;
                for (int k = 0; k < 63; k++) sum += pkt[k];
                pkt[63] = (byte)(0xFF - (sum & 0xFF));
                Write(pkt);
            }
        }

        void Write(byte[] pkt)
        {
            IAsyncResult ar = stream.BeginWrite(pkt, 0, pkt.Length, null, null);
            if (!ar.AsyncWaitHandle.WaitOne(250)) throw new IOException("Keyboard write timeout");
            stream.EndWrite(ar);
        }

        public void Dispose()
        {
            try { if (stream != null) stream.Dispose(); } catch { }
            try { if (handle != null) handle.Dispose(); } catch { }
            stream = null;
            handle = null;
        }

        // ---------------- HID / SetupAPI ----------------

        static bool QueryDevice(string path, out string product, out int outputReportLength)
        {
            product = "";
            outputReportLength = 0;
            using (var h = CreateFile(path, 0, FILE_SHARE_READ | FILE_SHARE_WRITE, IntPtr.Zero, OPEN_EXISTING, 0, IntPtr.Zero))
            {
                if (h.IsInvalid) return false;
                IntPtr pd;
                if (!HidD_GetPreparsedData(h, out pd)) return false;
                var caps = new byte[64];
                try { if (HidP_GetCaps(pd, caps) != HIDP_STATUS_SUCCESS) return false; }
                finally { HidD_FreePreparsedData(pd); }
                ushort usage = BitConverter.ToUInt16(caps, 0);
                ushort usagePage = BitConverter.ToUInt16(caps, 2);
                outputReportLength = BitConverter.ToUInt16(caps, 6);
                if (usagePage != UsagePage || usage != Usage) return false;

                var buf = new byte[256];
                if (HidD_GetProductString(h, buf, buf.Length))
                    product = Encoding.Unicode.GetString(buf).TrimEnd('\0');
                return true;
            }
        }

        static IEnumerable<string> EnumerateHidPaths()
        {
            Guid hidGuid;
            HidD_GetHidGuid(out hidGuid);
            IntPtr set = SetupDiGetClassDevs(ref hidGuid, IntPtr.Zero, IntPtr.Zero, DIGCF_PRESENT | DIGCF_DEVICEINTERFACE);
            if (set == new IntPtr(-1)) yield break;
            try
            {
                var ifData = new SP_DEVICE_INTERFACE_DATA();
                ifData.cbSize = Marshal.SizeOf(typeof(SP_DEVICE_INTERFACE_DATA));
                for (uint i = 0; SetupDiEnumDeviceInterfaces(set, IntPtr.Zero, ref hidGuid, i, ref ifData); i++)
                {
                    int required;
                    SetupDiGetDeviceInterfaceDetail(set, ref ifData, IntPtr.Zero, 0, out required, IntPtr.Zero);
                    if (required <= 0) continue;
                    IntPtr detail = Marshal.AllocHGlobal(required);
                    try
                    {
                        Marshal.WriteInt32(detail, IntPtr.Size == 8 ? 8 : 6);
                        if (!SetupDiGetDeviceInterfaceDetail(set, ref ifData, detail, required, out required, IntPtr.Zero)) continue;
                        string path = Marshal.PtrToStringUni(new IntPtr(detail.ToInt64() + 4));
                        if (!string.IsNullOrEmpty(path)) yield return path;
                    }
                    finally { Marshal.FreeHGlobal(detail); }
                }
            }
            finally { SetupDiDestroyDeviceInfoList(set); }
        }

        const uint GENERIC_READ = 0x80000000, GENERIC_WRITE = 0x40000000;
        const uint FILE_SHARE_READ = 1, FILE_SHARE_WRITE = 2, OPEN_EXISTING = 3, FILE_FLAG_OVERLAPPED = 0x40000000;
        const uint DIGCF_PRESENT = 0x2, DIGCF_DEVICEINTERFACE = 0x10;
        const int HIDP_STATUS_SUCCESS = 0x00110000;

        [StructLayout(LayoutKind.Sequential)]
        struct SP_DEVICE_INTERFACE_DATA
        {
            public int cbSize;
            public Guid InterfaceClassGuid;
            public int Flags;
            public IntPtr Reserved;
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        static extern SafeFileHandle CreateFile(string name, uint access, uint share, IntPtr security, uint creation, uint flags, IntPtr template);

        [DllImport("hid.dll")] static extern void HidD_GetHidGuid(out Guid guid);
        [DllImport("hid.dll")] static extern bool HidD_GetPreparsedData(SafeFileHandle h, out IntPtr data);
        [DllImport("hid.dll")] static extern bool HidD_FreePreparsedData(IntPtr data);
        [DllImport("hid.dll")] static extern int HidP_GetCaps(IntPtr data, byte[] caps);
        [DllImport("hid.dll")] static extern bool HidD_GetProductString(SafeFileHandle h, byte[] buffer, int length);

        [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        static extern IntPtr SetupDiGetClassDevs(ref Guid classGuid, IntPtr enumerator, IntPtr parent, uint flags);
        [DllImport("setupapi.dll", SetLastError = true)]
        static extern bool SetupDiEnumDeviceInterfaces(IntPtr set, IntPtr devInfo, ref Guid guid, uint index, ref SP_DEVICE_INTERFACE_DATA data);
        [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        static extern bool SetupDiGetDeviceInterfaceDetail(IntPtr set, ref SP_DEVICE_INTERFACE_DATA data, IntPtr detail, int size, out int required, IntPtr devInfo);
        [DllImport("setupapi.dll")]
        static extern bool SetupDiDestroyDeviceInfoList(IntPtr set);
    }
}
