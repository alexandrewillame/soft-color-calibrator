using System.Text.Json.Serialization;
using Windows.Win32;

namespace SoftCal.Windows.Gamma;

/// <summary>A GPU gamma ramp: 256 16-bit entries per channel, as GetDeviceGammaRamp/SetDeviceGammaRamp use them.</summary>
public sealed record GammaRamp
{
    public const int Size = 256;

    [JsonConstructor]
    public GammaRamp(ushort[] red, ushort[] green, ushort[] blue)
    {
        ArgumentNullException.ThrowIfNull(red);
        ArgumentNullException.ThrowIfNull(green);
        ArgumentNullException.ThrowIfNull(blue);
        if (red.Length != Size || green.Length != Size || blue.Length != Size)
        {
            throw new ArgumentException("A gamma ramp has 256 entries per channel.");
        }

        Red = red;
        Green = green;
        Blue = blue;
    }

    public ushort[] Red { get; }

    public ushort[] Green { get; }

    public ushort[] Blue { get; }

    public static GammaRamp Identity() => FromFunction(i => i / 255.0);

    /// <summary>Every channel follows <paramref name="map"/> (input and output both 0–1).</summary>
    public static GammaRamp FromFunction(Func<int, double> map)
    {
        ArgumentNullException.ThrowIfNull(map);
        var values = new ushort[Size];
        for (var i = 0; i < Size; i++)
        {
            values[i] = (ushort)Math.Clamp(Math.Round(map(i) * 65535), 0, 65535);
        }

        return new GammaRamp(values, (ushort[])values.Clone(), (ushort[])values.Clone());
    }

    /// <summary>The identity scaled by <paramref name="factor"/>: code i leaves the GPU as i·factor.</summary>
    public static GammaRamp Scaled(double factor) => FromFunction(i => i * factor / 255.0);

    /// <summary>Largest difference from the identity ramp, in 16-bit units.</summary>
    public int MaxDeviationFromIdentity()
    {
        var max = 0;
        for (var i = 0; i < Size; i++)
        {
            var identity = i * 257;
            max = Math.Max(max, Math.Max(Math.Abs(Red[i] - identity), Math.Max(Math.Abs(Green[i] - identity), Math.Abs(Blue[i] - identity))));
        }

        return max;
    }

    /// <summary>True when the ramp passes 8-bit codes through unchanged (within half an 8-bit step).</summary>
    public bool IsIdentity => MaxDeviationFromIdentity() < 128;

    public bool SameAs(GammaRamp other)
    {
        ArgumentNullException.ThrowIfNull(other);
        return Red.AsSpan().SequenceEqual(other.Red) && Green.AsSpan().SequenceEqual(other.Green) && Blue.AsSpan().SequenceEqual(other.Blue);
    }

    internal ushort[] ToInterleaved()
    {
        var all = new ushort[3 * Size];
        Red.CopyTo(all, 0);
        Green.CopyTo(all, Size);
        Blue.CopyTo(all, 2 * Size);
        return all;
    }

    internal static GammaRamp FromInterleaved(ReadOnlySpan<ushort> all) =>
        new(all[..Size].ToArray(), all[Size..(2 * Size)].ToArray(), all[(2 * Size)..].ToArray());
}

/// <summary>Reads and writes one display's gamma ramp.</summary>
public interface IGammaRampDevice
{
    string DeviceName { get; }

    GammaRamp Read();

    /// <summary>False when the driver rejects the ramp (SetDeviceGammaRamp can also return true and do nothing:
    /// only a camera check proves the ramp took effect).</summary>
    bool Write(GammaRamp ramp);
}

/// <summary>GDI gamma ramps on a monitor's device context (<c>CreateDC("\\.\DISPLAYn")</c>).</summary>
public sealed class GdiGammaRampDevice(string deviceName) : IGammaRampDevice
{
    public string DeviceName { get; } = deviceName ?? throw new ArgumentNullException(nameof(deviceName));

    public unsafe GammaRamp Read()
    {
        var hdc = PInvoke.CreateDCW(null, DeviceName, null, null);
        if (hdc.IsNull)
        {
            throw new InvalidOperationException($"CreateDC failed for {DeviceName}.");
        }

        try
        {
            var buffer = new ushort[3 * GammaRamp.Size];
            fixed (ushort* p = buffer)
            {
                if (!PInvoke.GetDeviceGammaRamp(hdc, p))
                {
                    throw new InvalidOperationException($"GetDeviceGammaRamp failed for {DeviceName}.");
                }
            }

            return GammaRamp.FromInterleaved(buffer);
        }
        finally
        {
            PInvoke.DeleteDC(hdc);
        }
    }

    public unsafe bool Write(GammaRamp ramp)
    {
        ArgumentNullException.ThrowIfNull(ramp);
        var hdc = PInvoke.CreateDCW(null, DeviceName, null, null);
        if (hdc.IsNull)
        {
            return false;
        }

        try
        {
            var buffer = ramp.ToInterleaved();
            fixed (ushort* p = buffer)
            {
                return PInvoke.SetDeviceGammaRamp(hdc, p);
            }
        }
        finally
        {
            PInvoke.DeleteDC(hdc);
        }
    }
}
