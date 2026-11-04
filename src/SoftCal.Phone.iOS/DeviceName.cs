using System.Runtime.InteropServices;

namespace SoftCal.Phone.iOS;

/// <summary>
/// The device's model as its owner knows it ("iPhone 15 Pro"), from its model identifier ("iPhone16,1"): iOS gives apps
/// neither the model's name nor (since iOS 16) the device's own name. A model newer than this list shows its identifier.
/// </summary>
internal static partial class DeviceName
{
    /// <summary>The iPhones that run iOS 16 or later.</summary>
    private static readonly Dictionary<string, string> Phones = new(StringComparer.Ordinal)
    {
        ["iPhone10,1"] = "iPhone 8",
        ["iPhone10,4"] = "iPhone 8",
        ["iPhone10,2"] = "iPhone 8 Plus",
        ["iPhone10,5"] = "iPhone 8 Plus",
        ["iPhone10,3"] = "iPhone X",
        ["iPhone10,6"] = "iPhone X",
        ["iPhone11,2"] = "iPhone XS",
        ["iPhone11,4"] = "iPhone XS Max",
        ["iPhone11,6"] = "iPhone XS Max",
        ["iPhone11,8"] = "iPhone XR",
        ["iPhone12,1"] = "iPhone 11",
        ["iPhone12,3"] = "iPhone 11 Pro",
        ["iPhone12,5"] = "iPhone 11 Pro Max",
        ["iPhone12,8"] = "iPhone SE (2nd generation)",
        ["iPhone13,1"] = "iPhone 12 mini",
        ["iPhone13,2"] = "iPhone 12",
        ["iPhone13,3"] = "iPhone 12 Pro",
        ["iPhone13,4"] = "iPhone 12 Pro Max",
        ["iPhone14,4"] = "iPhone 13 mini",
        ["iPhone14,5"] = "iPhone 13",
        ["iPhone14,2"] = "iPhone 13 Pro",
        ["iPhone14,3"] = "iPhone 13 Pro Max",
        ["iPhone14,6"] = "iPhone SE (3rd generation)",
        ["iPhone14,7"] = "iPhone 14",
        ["iPhone14,8"] = "iPhone 14 Plus",
        ["iPhone15,2"] = "iPhone 14 Pro",
        ["iPhone15,3"] = "iPhone 14 Pro Max",
        ["iPhone15,4"] = "iPhone 15",
        ["iPhone15,5"] = "iPhone 15 Plus",
        ["iPhone16,1"] = "iPhone 15 Pro",
        ["iPhone16,2"] = "iPhone 15 Pro Max",
        ["iPhone17,3"] = "iPhone 16",
        ["iPhone17,4"] = "iPhone 16 Plus",
        ["iPhone17,1"] = "iPhone 16 Pro",
        ["iPhone17,2"] = "iPhone 16 Pro Max",
        ["iPhone17,5"] = "iPhone 16e",
    };

    /// <summary>The model: "iPhone 15 Pro", or "iPhone (iPhone18,3)" / "iPad (iPad16,3)" for one this list does not
    /// know.</summary>
    public static string Model()
    {
        // The simulator runs on the Mac's processor and says which device it plays.
        var identifier = Environment.GetEnvironmentVariable("SIMULATOR_MODEL_IDENTIFIER") ?? Machine() ?? string.Empty;
        if (Phones.TryGetValue(identifier, out var name))
        {
            return name;
        }

        var kind = UIDevice.CurrentDevice.Model is { Length: > 0 } model ? model : "iPhone";
        return identifier.Length == 0 ? kind : $"{kind} ({identifier})";
    }

    /// <summary>The model identifier ("iPhone16,1"), from the kernel.</summary>
    private static unsafe string? Machine()
    {
        nuint length = 0;
        if (SysctlByName("hw.machine", null, &length, IntPtr.Zero, 0) != 0 || length is 0 or > 256)
        {
            return null;
        }

        var bytes = stackalloc byte[(int)length];
        return SysctlByName("hw.machine", bytes, &length, IntPtr.Zero, 0) == 0 ? Marshal.PtrToStringUTF8((IntPtr)bytes) : null;
    }

    [LibraryImport("/usr/lib/libSystem.dylib", EntryPoint = "sysctlbyname", StringMarshalling = StringMarshalling.Utf8)]
    private static unsafe partial int SysctlByName(string name, byte* oldp, nuint* oldlenp, IntPtr newp, nuint newlen);
}
