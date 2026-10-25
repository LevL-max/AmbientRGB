using System;
using System.Runtime.InteropServices;

namespace AmbientRgb
{
    /// <summary>
    /// Reads the peak level of the default playback device (what you hear) via IAudioMeterInformation.
    /// No audio capture stream is opened, so it costs next to nothing.
    /// </summary>
    public sealed class AudioMeter : IDisposable
    {
        const int eRender = 0;
        const int eConsole = 0;
        const uint CLSCTX_ALL = 23;

        IMMDeviceEnumerator enumerator;
        IMMDevice device;
        IAudioMeterInformation meter;

        public string DeviceId { get; private set; }

        public AudioMeter()
        {
            enumerator = (IMMDeviceEnumerator)new MMDeviceEnumeratorComObject();
            Open();
        }

        void Open()
        {
            IMMDevice dev;
            int hr = enumerator.GetDefaultAudioEndpoint(eRender, eConsole, out dev);
            if (hr < 0 || dev == null) throw new COMException("No default playback device", hr);
            string id;
            dev.GetId(out id);
            Guid iid = typeof(IAudioMeterInformation).GUID;
            object o;
            hr = dev.Activate(ref iid, CLSCTX_ALL, IntPtr.Zero, out o);
            if (hr < 0) { Marshal.ReleaseComObject(dev); throw new COMException("IAudioMeterInformation activation failed", hr); }
            ReleaseDevice();
            device = dev;
            meter = (IAudioMeterInformation)o;
            DeviceId = id;
        }

        /// <summary>Re-opens the meter if Windows switched the default output (e.g. headphones plugged in).</summary>
        public void CheckDefaultDevice()
        {
            IMMDevice dev;
            if (enumerator.GetDefaultAudioEndpoint(eRender, eConsole, out dev) < 0 || dev == null) return;
            string id;
            dev.GetId(out id);
            Marshal.ReleaseComObject(dev);
            if (id != DeviceId) Open();
        }

        public float GetPeak()
        {
            float p;
            if (meter == null || meter.GetPeakValue(out p) < 0) return 0f;
            return p;
        }

        void ReleaseDevice()
        {
            if (meter != null) { Marshal.ReleaseComObject(meter); meter = null; }
            if (device != null) { Marshal.ReleaseComObject(device); device = null; }
        }

        public void Dispose()
        {
            ReleaseDevice();
            if (enumerator != null) { Marshal.ReleaseComObject(enumerator); enumerator = null; }
        }

        [ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
        class MMDeviceEnumeratorComObject { }

        [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IMMDeviceEnumerator
        {
            [PreserveSig] int EnumAudioEndpoints(int dataFlow, int stateMask, out IntPtr devices);
            [PreserveSig] int GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice device);
        }

        [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IMMDevice
        {
            [PreserveSig] int Activate(ref Guid iid, uint clsCtx, IntPtr activationParams, [MarshalAs(UnmanagedType.IUnknown)] out object iface);
            [PreserveSig] int OpenPropertyStore(int access, out IntPtr properties);
            [PreserveSig] int GetId([MarshalAs(UnmanagedType.LPWStr)] out string id);
            [PreserveSig] int GetState(out int state);
        }

        [ComImport, Guid("C02216F6-8C67-4B5B-9D00-D008E73E0064"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IAudioMeterInformation
        {
            [PreserveSig] int GetPeakValue(out float peak);
            [PreserveSig] int GetMeteringChannelCount(out int count);
            [PreserveSig] int GetChannelsPeakValues(int count, [Out] float[] peaks);
            [PreserveSig] int QueryHardwareSupport(out int mask);
        }
    }
}
