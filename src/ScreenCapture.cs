using System;
using System.Runtime.InteropServices;

namespace AmbientRgb
{
    /// <summary>
    /// Captures the primary monitor with DXGI Desktop Duplication and shrinks the frame on the GPU
    /// (mip chain), so the CPU only reads a ~60x34 image per frame.
    /// </summary>
    public sealed class ScreenCapture : IDisposable
    {
        const int DXGI_ERROR_WAIT_TIMEOUT = unchecked((int)0x887A0027);
        const uint D3D11_BIND_SHADER_RESOURCE = 0x8;
        const uint D3D11_BIND_RENDER_TARGET = 0x20;
        const uint D3D11_RESOURCE_MISC_GENERATE_MIPS = 0x1;
        const uint D3D11_USAGE_STAGING = 3;
        const uint D3D11_CPU_ACCESS_READ = 0x20000;
        const int D3D11_MAP_READ = 1;
        const int MaxSampleWidth = 96;

        static Guid IID_ID3D11Texture2D = new Guid("6f15aaf2-d208-4e89-9ab4-489535d34f9c");
        static Guid IID_IDXGIFactory1 = new Guid("770aae78-f26f-4dba-a829-253c83d1b387");

        IntPtr devicePtr, contextPtr;
        object deviceObj;
        ID3D11Device device;
        ID3D11DeviceContext context;
        IDXGIOutputDuplication dupl;
        IntPtr mipTex, mipSrv, staging;
        int mipLevel;

        public int Width { get; private set; }
        public int Height { get; private set; }
        public byte[] Pixels { get; private set; }   // tightly packed BGRA, Width*Height*4
        public string OutputName { get; private set; }

        public ScreenCapture()
        {
            try { Init(); }
            catch { Dispose(); throw; }
        }

        void Init()
        {
            IntPtr factoryPtr;
            Check(CreateDXGIFactory1(ref IID_IDXGIFactory1, out factoryPtr), "CreateDXGIFactory1");
            var factory = (IDXGIFactory1)Marshal.GetObjectForIUnknown(factoryPtr);
            Marshal.Release(factoryPtr);

            IDXGIAdapter1 chosenAdapter = null;
            IDXGIOutput chosenOutput = null;
            try
            {
                for (uint a = 0; chosenOutput == null; a++)
                {
                    IDXGIAdapter1 adapter;
                    if (factory.EnumAdapters1(a, out adapter) < 0) break;
                    for (uint o = 0; ; o++)
                    {
                        IDXGIOutput output;
                        if (adapter.EnumOutputs(o, out output) < 0) break;
                        DXGI_OUTPUT_DESC desc;
                        output.GetDesc(out desc);
                        if (chosenOutput == null && desc.AttachedToDesktop != 0 && desc.Left == 0 && desc.Top == 0)
                        {
                            chosenOutput = output;
                            OutputName = desc.DeviceName + " " + (desc.Right - desc.Left) + "x" + (desc.Bottom - desc.Top);
                        }
                        else Marshal.ReleaseComObject(output);
                    }
                    if (chosenOutput != null) chosenAdapter = adapter;
                    else Marshal.ReleaseComObject(adapter);
                }
                if (chosenOutput == null) throw new InvalidOperationException("Primary display not found");

                IntPtr adapterPtr = Marshal.GetComInterfaceForObject(chosenAdapter, typeof(IDXGIAdapter1));
                try
                {
                    int featureLevel;
                    Check(D3D11CreateDevice(adapterPtr, 0 /* D3D_DRIVER_TYPE_UNKNOWN */, IntPtr.Zero, 0, null, 0, 7,
                        out devicePtr, out featureLevel, out contextPtr), "D3D11CreateDevice");
                }
                finally { Marshal.Release(adapterPtr); }

                deviceObj = Marshal.GetObjectForIUnknown(devicePtr);
                device = (ID3D11Device)deviceObj;
                context = (ID3D11DeviceContext)Marshal.GetObjectForIUnknown(contextPtr);

                var output1 = (IDXGIOutput1)chosenOutput;
                Check(output1.DuplicateOutput(deviceObj, out dupl), "DuplicateOutput");
            }
            finally
            {
                if (chosenOutput != null) Marshal.ReleaseComObject(chosenOutput);
                if (chosenAdapter != null) Marshal.ReleaseComObject(chosenAdapter);
                Marshal.ReleaseComObject(factory);
            }
        }

        /// <summary>Returns true when a new desktop frame was captured into <see cref="Pixels"/>.</summary>
        public bool TryCapture()
        {
            DXGI_OUTDUPL_FRAME_INFO info;
            IntPtr resource;
            int hr = dupl.AcquireNextFrame(0, out info, out resource);
            if (hr == DXGI_ERROR_WAIT_TIMEOUT) return false;
            Check(hr, "AcquireNextFrame");
            try
            {
                if (info.LastPresentTime == 0) return false; // only the mouse pointer changed
                IntPtr tex;
                Check(Marshal.QueryInterface(resource, ref IID_ID3D11Texture2D, out tex), "QueryInterface(ID3D11Texture2D)");
                try
                {
                    if (mipTex == IntPtr.Zero) CreateResources(tex);
                    context.CopySubresourceRegion(mipTex, 0, 0, 0, 0, tex, 0, IntPtr.Zero);
                }
                finally { Marshal.Release(tex); }
            }
            finally
            {
                if (resource != IntPtr.Zero) Marshal.Release(resource);
                dupl.ReleaseFrame();
            }

            context.GenerateMips(mipSrv);
            context.CopySubresourceRegion(staging, 0, 0, 0, 0, mipTex, (uint)mipLevel, IntPtr.Zero);

            D3D11_MAPPED_SUBRESOURCE map;
            Check(context.Map(staging, 0, D3D11_MAP_READ, 0, out map), "Map");
            try
            {
                int rowBytes = Width * 4;
                for (int y = 0; y < Height; y++)
                    Marshal.Copy(new IntPtr(map.pData.ToInt64() + (long)y * map.RowPitch), Pixels, y * rowBytes, rowBytes);
            }
            finally { context.Unmap(staging, 0); }
            return true;
        }

        void CreateResources(IntPtr frameTex)
        {
            var texObj = (ID3D11Texture2D)Marshal.GetObjectForIUnknown(frameTex);
            D3D11_TEXTURE2D_DESC fd;
            texObj.GetDesc(out fd);
            Marshal.ReleaseComObject(texObj);

            mipLevel = 0;
            while ((fd.Width >> mipLevel) > MaxSampleWidth) mipLevel++;
            Width = Math.Max(1, (int)(fd.Width >> mipLevel));
            Height = Math.Max(1, (int)(fd.Height >> mipLevel));
            Pixels = new byte[Width * Height * 4];

            var md = new D3D11_TEXTURE2D_DESC
            {
                Width = fd.Width, Height = fd.Height, MipLevels = (uint)mipLevel + 1, ArraySize = 1,
                Format = fd.Format, SampleCount = 1,
                BindFlags = D3D11_BIND_SHADER_RESOURCE | D3D11_BIND_RENDER_TARGET,
                MiscFlags = D3D11_RESOURCE_MISC_GENERATE_MIPS
            };
            Check(device.CreateTexture2D(ref md, IntPtr.Zero, out mipTex), "CreateTexture2D(mips)");
            Check(device.CreateShaderResourceView(mipTex, IntPtr.Zero, out mipSrv), "CreateShaderResourceView");

            var sd = new D3D11_TEXTURE2D_DESC
            {
                Width = (uint)Width, Height = (uint)Height, MipLevels = 1, ArraySize = 1,
                Format = fd.Format, SampleCount = 1,
                Usage = D3D11_USAGE_STAGING, CPUAccessFlags = D3D11_CPU_ACCESS_READ
            };
            Check(device.CreateTexture2D(ref sd, IntPtr.Zero, out staging), "CreateTexture2D(staging)");
        }

        public void Dispose()
        {
            ReleasePtr(ref staging);
            ReleasePtr(ref mipSrv);
            ReleasePtr(ref mipTex);
            ReleaseRcw(dupl); dupl = null;
            ReleaseRcw(context); context = null;
            ReleaseRcw(deviceObj); deviceObj = null; device = null;
            ReleasePtr(ref contextPtr);
            ReleasePtr(ref devicePtr);
        }

        static void ReleasePtr(ref IntPtr p)
        {
            if (p != IntPtr.Zero) { Marshal.Release(p); p = IntPtr.Zero; }
        }

        static void ReleaseRcw(object o)
        {
            if (o != null) { try { Marshal.ReleaseComObject(o); } catch { } }
        }

        static void Check(int hr, string what)
        {
            if (hr < 0) throw new COMException(what + " failed (0x" + hr.ToString("X8") + ")", hr);
        }

        // ---------------- interop ----------------

        [DllImport("dxgi.dll")]
        static extern int CreateDXGIFactory1(ref Guid riid, out IntPtr factory);

        [DllImport("d3d11.dll")]
        static extern int D3D11CreateDevice(IntPtr adapter, int driverType, IntPtr software, uint flags,
            int[] featureLevels, uint numFeatureLevels, uint sdkVersion,
            out IntPtr device, out int featureLevel, out IntPtr immediateContext);

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        struct DXGI_OUTPUT_DESC
        {
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string DeviceName;
            public int Left, Top, Right, Bottom;
            public int AttachedToDesktop;
            public int Rotation;
            public IntPtr Monitor;
        }

        [StructLayout(LayoutKind.Sequential)]
        struct DXGI_OUTDUPL_FRAME_INFO
        {
            public long LastPresentTime;
            public long LastMouseUpdateTime;
            public uint AccumulatedFrames;
            public int RectsCoalesced;
            public int ProtectedContentMaskedOut;
            public int PointerX, PointerY;
            public int PointerVisible;
            public uint TotalMetadataBufferSize;
            public uint PointerShapeBufferSize;
        }

        [StructLayout(LayoutKind.Sequential)]
        struct D3D11_TEXTURE2D_DESC
        {
            public uint Width, Height, MipLevels, ArraySize, Format, SampleCount, SampleQuality;
            public uint Usage, BindFlags, CPUAccessFlags, MiscFlags;
        }

        [StructLayout(LayoutKind.Sequential)]
        struct D3D11_MAPPED_SUBRESOURCE
        {
            public IntPtr pData;
            public uint RowPitch;
            public uint DepthPitch;
        }

        [ComImport, Guid("770aae78-f26f-4dba-a829-253c83d1b387"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IDXGIFactory1
        {
            void _SetPrivateData(); void _SetPrivateDataInterface(); void _GetPrivateData(); void _GetParent();
            void _EnumAdapters(); void _MakeWindowAssociation(); void _GetWindowAssociation();
            void _CreateSwapChain(); void _CreateSoftwareAdapter();
            [PreserveSig] int EnumAdapters1(uint index, out IDXGIAdapter1 adapter);
            [PreserveSig] int IsCurrent();
        }

        [ComImport, Guid("29038f61-3839-4626-91fd-086879011a05"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IDXGIAdapter1
        {
            void _SetPrivateData(); void _SetPrivateDataInterface(); void _GetPrivateData(); void _GetParent();
            [PreserveSig] int EnumOutputs(uint index, out IDXGIOutput output);
            void _GetDesc(); void _CheckInterfaceSupport(); void _GetDesc1();
        }

        [ComImport, Guid("ae02eedb-c735-4690-8d52-5a8dc20213aa"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IDXGIOutput
        {
            void _SetPrivateData(); void _SetPrivateDataInterface(); void _GetPrivateData(); void _GetParent();
            [PreserveSig] int GetDesc(out DXGI_OUTPUT_DESC desc);
        }

        [ComImport, Guid("00cddea8-939b-4b83-a340-a685226666cc"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IDXGIOutput1
        {
            void _SetPrivateData(); void _SetPrivateDataInterface(); void _GetPrivateData(); void _GetParent();
            [PreserveSig] int GetDesc(out DXGI_OUTPUT_DESC desc);
            void _GetDisplayModeList(); void _FindClosestMatchingMode(); void _WaitForVBlank();
            void _TakeOwnership(); void _ReleaseOwnership(); void _GetGammaControlCapabilities();
            void _SetGammaControl(); void _GetGammaControl(); void _SetDisplaySurface();
            void _GetDisplaySurfaceData(); void _GetFrameStatistics();
            void _GetDisplayModeList1(); void _FindClosestMatchingMode1(); void _GetDisplaySurfaceData1();
            [PreserveSig] int DuplicateOutput([MarshalAs(UnmanagedType.IUnknown)] object device, out IDXGIOutputDuplication duplication);
        }

        [ComImport, Guid("191cfac3-a341-470d-b26e-a864f428319c"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IDXGIOutputDuplication
        {
            void _SetPrivateData(); void _SetPrivateDataInterface(); void _GetPrivateData(); void _GetParent();
            void _GetDesc();
            [PreserveSig] int AcquireNextFrame(uint timeoutMs, out DXGI_OUTDUPL_FRAME_INFO info, out IntPtr resource);
            void _GetFrameDirtyRects(); void _GetFrameMoveRects(); void _GetFramePointerShape();
            void _MapDesktopSurface(); void _UnMapDesktopSurface();
            [PreserveSig] int ReleaseFrame();
        }

        [ComImport, Guid("db6f6ddb-ac77-4e88-8253-819df9bbf140"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface ID3D11Device
        {
            void _CreateBuffer(); void _CreateTexture1D();
            [PreserveSig] int CreateTexture2D(ref D3D11_TEXTURE2D_DESC desc, IntPtr initialData, out IntPtr texture);
            void _CreateTexture3D();
            [PreserveSig] int CreateShaderResourceView(IntPtr resource, IntPtr desc, out IntPtr view);
        }

        [ComImport, Guid("6f15aaf2-d208-4e89-9ab4-489535d34f9c"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface ID3D11Texture2D
        {
            void _GetDevice(); void _GetPrivateData(); void _SetPrivateData(); void _SetPrivateDataInterface();
            void _GetType(); void _SetEvictionPriority(); void _GetEvictionPriority();
            [PreserveSig] void GetDesc(out D3D11_TEXTURE2D_DESC desc);
        }

        // Vtable order from d3d11.h; only Map/Unmap/CopySubresourceRegion/GenerateMips are called.
        [ComImport, Guid("c0bfa96c-e089-44fb-8eaf-26f8796190da"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface ID3D11DeviceContext
        {
            void _GetDevice(); void _GetPrivateData(); void _SetPrivateData(); void _SetPrivateDataInterface(); // 3-6
            void _VSSetConstantBuffers(); void _PSSetShaderResources(); void _PSSetShader(); void _PSSetSamplers(); // 7-10
            void _VSSetShader(); void _DrawIndexed(); void _Draw();                                              // 11-13
            [PreserveSig] int Map(IntPtr resource, uint subresource, int mapType, uint mapFlags, out D3D11_MAPPED_SUBRESOURCE mapped); // 14
            [PreserveSig] void Unmap(IntPtr resource, uint subresource);                                          // 15
            void _m16(); void _m17(); void _m18(); void _m19(); void _m20(); void _m21(); void _m22(); void _m23();
            void _m24(); void _m25(); void _m26(); void _m27(); void _m28(); void _m29(); void _m30(); void _m31();
            void _m32(); void _m33(); void _m34(); void _m35(); void _m36(); void _m37(); void _m38(); void _m39();
            void _m40(); void _m41(); void _m42(); void _m43(); void _m44(); void _m45();
            [PreserveSig] void CopySubresourceRegion(IntPtr dst, uint dstSubresource, uint dstX, uint dstY, uint dstZ,
                IntPtr src, uint srcSubresource, IntPtr srcBox);                                                 // 46
            [PreserveSig] void CopyResource(IntPtr dst, IntPtr src);                                              // 47
            void _m48(); void _m49(); void _m50(); void _m51(); void _m52(); void _m53();
            [PreserveSig] void GenerateMips(IntPtr shaderResourceView);                                           // 54
        }
    }
}
