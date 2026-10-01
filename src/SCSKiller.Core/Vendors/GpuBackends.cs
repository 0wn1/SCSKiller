using System.Runtime.InteropServices;

namespace SCSKiller.Core.Vendors;

public static class GpuBackends
{
    /// <summary>Backend for the primary discrete adapter. Adding a vendor = one case here.</summary>
    public static IGpuVendorBackend Detect()
    {
        var gpu = PrimaryAdapter() ?? new GpuInfo(GpuVendor.Unknown, "no D3D adapter", "", 0, 0);
        try
        {
            return gpu.Vendor switch
            {
                GpuVendor.Nvidia => new NvidiaBackend(gpu),
                GpuVendor.Amd => new AmdBackend(gpu),
                _ => new UnsupportedVendor(gpu),
            };
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException or InvalidOperationException)
        {
            return new UnsupportedVendor(gpu);   // e.g. NVIDIA GPU without a working nvapi64.dll
        }
    }

    /// <summary>The non-software adapter with the most dedicated VRAM (what games render on), the same rule as
    /// scskiller_warm. DriverVersion is the user-mode driver file version (vendor backends replace it).</summary>
    public static unsafe GpuInfo? PrimaryAdapter()
    {
        var iid = new Guid("770aae78-f26f-4dba-a829-253c83d1b387");   // IDXGIFactory1
        if (CreateDXGIFactory1(&iid, out var factory) < 0) return null;
        try
        {
            GpuInfo? best = null;
            var enumAdapters1 = (delegate* unmanaged<nint, uint, nint*, int>)(*(nint**)factory)[12];
            nint adapter;
            for (uint i = 0; enumAdapters1(factory, i, &adapter) >= 0; i++)
            {
                var vt = *(nint**)adapter;
                AdapterDesc1 d;
                ((delegate* unmanaged<nint, AdapterDesc1*, int>)vt[10])(adapter, &d);   // GetDesc1
                var dxgiDevice = new Guid("54ec77fa-1377-44e6-8c32-88fd5f44c84c");     // IDXGIDevice: yields the UMD version
                long umd;
                var version = ((delegate* unmanaged<nint, Guid*, long*, int>)vt[9])(adapter, &dxgiDevice, &umd) >= 0   // CheckInterfaceSupport
                    ? $"{umd >> 48 & 0xFFFF}.{umd >> 32 & 0xFFFF}.{umd >> 16 & 0xFFFF}.{umd & 0xFFFF}" : "";
                ((delegate* unmanaged<nint, uint>)vt[2])(adapter);   // Release
                if ((d.Flags & 2) != 0 || (best != null && d.DedicatedVideoMemory <= best.DedicatedVideoMemory)) continue;   // 2 = DXGI_ADAPTER_FLAG_SOFTWARE
                var vendor = Enum.IsDefined(typeof(GpuVendor), (int)d.VendorId) ? (GpuVendor)d.VendorId : GpuVendor.Unknown;
                best = new GpuInfo(vendor, new string(d.Description), version, ((long)d.LuidHigh << 32) | d.LuidLow, d.DedicatedVideoMemory);
            }
            return best;
        }
        finally { ((delegate* unmanaged<nint, uint>)(*(nint**)factory)[2])(factory); }
    }

    [DllImport("dxgi.dll")] static extern unsafe int CreateDXGIFactory1(Guid* riid, out nint factory);

    unsafe struct AdapterDesc1
    {
        public fixed char Description[128];
        public uint VendorId, DeviceId, SubSysId, Revision;
        public nuint DedicatedVideoMemory, DedicatedSystemMemory, SharedSystemMemory;
        public uint LuidLow;
        public int LuidHigh;
        public uint Flags;
    }
}

/// <summary>Intel / unknown until measured: no cache assumptions, warming reports Unsupported via the caps.</summary>
public sealed class UnsupportedVendor(GpuInfo gpu) : IGpuVendorBackend
{
    public GpuVendor Vendor => Gpu.Vendor;
    public GpuInfo Gpu { get; } = gpu;
    public VendorCaps Caps { get; } = new("unsupported", false, false, false);
    public CacheUsage GetCacheUsage() => new("", 0, true);
    public CacheLimit? GetCacheLimit() => null;
    public void SetCacheLimit(CacheLimit limit) => throw new NotSupportedException($"cache size is not configurable for {Gpu.Name}");
}
