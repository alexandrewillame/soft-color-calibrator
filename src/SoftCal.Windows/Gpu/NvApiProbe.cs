using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;

namespace SoftCal.Windows.Gpu;

/// <summary>NVIDIA's view of a display link: pixel encoding, colorimetry, RGB range and bit depth.</summary>
public sealed record NvColorData(
    string ColorFormat,
    string Colorimetry,
    string DynamicRange,
    string BitsPerComponent,
    string SelectionPolicy,
    string? DesktopDepth,
    int StructVersion)
{
    /// <summary>Full-range RGB 4:4:4, the only link format under which halftones and code values survive unchanged.</summary>
    public bool IsFullRangeRgb => ColorFormat == "RGB" && DynamicRange == "full (VESA)";
}

public sealed record NvDisplayReport(string DeviceName, uint? DisplayId, NvColorData? Current, NvColorData? Default, string? Error);

public sealed record NvApiReport(
    bool Available,
    string? InterfaceVersion,
    string? DriverVersion,
    IReadOnlyList<string> Gpus,
    IReadOnlyList<NvDisplayReport> Displays,
    string? Error);

/// <summary>
/// Read-only NVAPI probe of the link format per display (NvAPI_Disp_ColorControl with the GET command). NVAPI is not a
/// Win32 API and has no metadata for CsWin32: it is reached through nvapi64.dll's <c>nvapi_QueryInterface</c> with the
/// function IDs and structure layouts of NVIDIA's MIT-licensed NVAPI headers.
/// </summary>
public static unsafe class NvApiProbe
{
    // Function IDs from nvapi_interface.h.
    private const uint IdInitialize = 0x0150E828;
    private const uint IdUnload = 0xD22BDD7E;
    private const uint IdGetInterfaceVersionString = 0x01053FA5;
    private const uint IdSysGetDriverAndBranchVersion = 0x2926AAAD;
    private const uint IdEnumPhysicalGpus = 0xE5AC921F;
    private const uint IdGpuGetFullName = 0xCEEE8E9F;
    private const uint IdDispGetDisplayIdByDisplayName = 0xAE457190;
    private const uint IdDispColorControl = 0x92F9D80D;

    private const byte CmdGet = 1;
    private const byte CmdGetDefault = 4;
    private const int IncompatibleStructVersion = -9;

    public static NvApiReport Probe(IEnumerable<string> gdiDeviceNames)
    {
        ArgumentNullException.ThrowIfNull(gdiDeviceNames);
        if (!NativeLibrary.TryLoad("nvapi64.dll", out var library))
        {
            return new NvApiReport(false, null, null, [], [], "nvapi64.dll not found (no NVIDIA driver)");
        }

        try
        {
            if (!NativeLibrary.TryGetExport(library, "nvapi_QueryInterface", out var queryAddress))
            {
                return new NvApiReport(false, null, null, [], [], "nvapi_QueryInterface not exported");
            }

            var query = (delegate* unmanaged[Cdecl]<uint, nint>)queryAddress;
            var initialize = (delegate* unmanaged[Cdecl]<int>)query(IdInitialize);
            if (initialize == null || initialize() != 0)
            {
                return new NvApiReport(false, null, null, [], [], "NvAPI_Initialize failed");
            }

            try
            {
                return new NvApiReport(
                    true,
                    InterfaceVersion(query),
                    DriverVersion(query),
                    Gpus(query),
                    [.. gdiDeviceNames.Select(name => Display(query, name))],
                    null);
            }
            finally
            {
                var unload = (delegate* unmanaged[Cdecl]<int>)query(IdUnload);
                if (unload != null)
                {
                    _ = unload();
                }
            }
        }
        finally
        {
            NativeLibrary.Free(library);
        }
    }

    /// <summary>NV_COLOR_DATA_V5: version, size, cmd, then { format, colorimetry, range (bytes); bpc, policy, depth (ints) }.</summary>
    [StructLayout(LayoutKind.Explicit, Size = 24)]
    internal struct ColorDataV5
    {
        [FieldOffset(0)]
        public uint Version;

        [FieldOffset(4)]
        public ushort Size;

        [FieldOffset(6)]
        public byte Cmd;

        [FieldOffset(8)]
        public byte ColorFormat;

        [FieldOffset(9)]
        public byte Colorimetry;

        [FieldOffset(10)]
        public byte DynamicRange;

        [FieldOffset(12)]
        public int Bpc;

        [FieldOffset(16)]
        public int SelectionPolicy;

        [FieldOffset(20)]
        public int Depth;
    }

    /// <summary>MAKE_NVAPI_VERSION(type, version) = sizeof(type) | version &lt;&lt; 16.</summary>
    internal static uint MakeVersion(int size, int version) => (uint)size | ((uint)version << 16);

    internal static NvColorData Describe(in ColorDataV5 data, int structVersion) => new(
        data.ColorFormat switch
        {
            0 => "RGB",
            1 => "YCbCr 4:2:2",
            2 => "YCbCr 4:4:4",
            3 => "YCbCr 4:2:0",
            0xFE => "default",
            0xFF => "auto",
            _ => $"unknown ({data.ColorFormat})",
        },
        data.Colorimetry switch
        {
            0 => "RGB",
            1 => "YCC601",
            2 => "YCC709",
            3 => "xvYCC601",
            4 => "xvYCC709",
            5 => "sYCC601",
            6 => "AdobeYCC601",
            7 => "AdobeRGB",
            8 => "BT2020RGB",
            9 => "BT2020YCC",
            10 => "BT2020cYCC",
            0xFE => "default",
            0xFF => "auto",
            _ => $"unknown ({data.Colorimetry})",
        },
        data.DynamicRange switch
        {
            0 => "full (VESA)",
            1 => "limited (CEA)",
            0xFF => "auto",
            _ => $"unknown ({data.DynamicRange})",
        },
        data.Bpc switch
        {
            0 => "default",
            1 => "6",
            2 => "8",
            3 => "10",
            4 => "12",
            5 => "16",
            _ => $"unknown ({data.Bpc})",
        },
        data.SelectionPolicy switch
        {
            0 => "user",
            1 => "best quality (driver/OS)",
            0xFF => "unknown",
            _ => $"unknown ({data.SelectionPolicy})",
        },
        structVersion < 5
            ? null
            : data.Depth switch
            {
                0 => "default",
                1 => "8 bpc",
                2 => "10 bpc",
                3 => "16 bpc float",
                4 => "16 bpc float WCG",
                5 => "16 bpc float HDR",
                _ => $"unknown ({data.Depth})",
            },
        structVersion);

    private static NvDisplayReport Display(delegate* unmanaged[Cdecl]<uint, nint> query, string deviceName)
    {
        var byName = (delegate* unmanaged[Cdecl]<byte*, uint*, int>)query(IdDispGetDisplayIdByDisplayName);
        var control = (delegate* unmanaged[Cdecl]<uint, ColorDataV5*, int>)query(IdDispColorControl);
        if (byName == null || control == null)
        {
            return new NvDisplayReport(deviceName, null, null, null, "the driver does not export the display functions");
        }

        var name = Encoding.ASCII.GetBytes(deviceName + "\0");
        uint displayId;
        int status;
        fixed (byte* p = name)
        {
            status = byName(p, &displayId);
        }

        if (status != 0)
        {
            return new NvDisplayReport(deviceName, null, null, null, string.Create(CultureInfo.InvariantCulture, $"NvAPI_DISP_GetDisplayIdByDisplayName returned {status}"));
        }

        var current = Get(control, displayId, CmdGet, out var currentStatus);
        var defaults = Get(control, displayId, CmdGetDefault, out _);
        return new NvDisplayReport(
            deviceName,
            displayId,
            current,
            defaults,
            current is null ? string.Create(CultureInfo.InvariantCulture, $"NvAPI_Disp_ColorControl returned {currentStatus}") : null);
    }

    private static NvColorData? Get(delegate* unmanaged[Cdecl]<uint, ColorDataV5*, int> control, uint displayId, byte cmd, out int status)
    {
        // Try V5 (24 bytes) first, then V4 (20 bytes: no desktop depth) for older drivers.
        foreach (var (version, size) in new[] { (5, 24), (4, 20) })
        {
            var data = new ColorDataV5 { Version = MakeVersion(size, version), Size = (ushort)size, Cmd = cmd };
            status = control(displayId, &data);
            if (status == 0)
            {
                return Describe(data, version);
            }

            if (status != IncompatibleStructVersion)
            {
                return null;
            }
        }

        status = IncompatibleStructVersion;
        return null;
    }

    private static string? InterfaceVersion(delegate* unmanaged[Cdecl]<uint, nint> query)
    {
        var f = (delegate* unmanaged[Cdecl]<byte*, int>)query(IdGetInterfaceVersionString);
        if (f == null)
        {
            return null;
        }

        var buffer = stackalloc byte[64];
        return f(buffer) == 0 ? ShortString(buffer) : null;
    }

    private static string? DriverVersion(delegate* unmanaged[Cdecl]<uint, nint> query)
    {
        var f = (delegate* unmanaged[Cdecl]<uint*, byte*, int>)query(IdSysGetDriverAndBranchVersion);
        if (f == null)
        {
            return null;
        }

        uint version;
        var branch = stackalloc byte[64];
        return f(&version, branch) == 0
            ? string.Create(CultureInfo.InvariantCulture, $"{version / 100}.{version % 100:00} ({ShortString(branch)})")
            : null;
    }

    private static List<string> Gpus(delegate* unmanaged[Cdecl]<uint, nint> query)
    {
        var enumerate = (delegate* unmanaged[Cdecl]<nint*, uint*, int>)query(IdEnumPhysicalGpus);
        var fullName = (delegate* unmanaged[Cdecl]<nint, byte*, int>)query(IdGpuGetFullName);
        var names = new List<string>();
        if (enumerate == null || fullName == null)
        {
            return names;
        }

        var handles = stackalloc nint[64];
        uint count;
        if (enumerate(handles, &count) != 0)
        {
            return names;
        }

        var buffer = stackalloc byte[64];
        for (var i = 0; i < count && i < 64; i++)
        {
            if (fullName(handles[i], buffer) == 0)
            {
                names.Add(ShortString(buffer));
            }
        }

        return names;
    }

    private static string ShortString(byte* buffer)
    {
        var length = 0;
        while (length < 64 && buffer[length] != 0)
        {
            length++;
        }

        return Encoding.ASCII.GetString(buffer, length);
    }
}
