using System.Runtime.InteropServices;
using Windows.Win32;
using Windows.Win32.Media.MediaFoundation;

namespace SoftCal.Instruments.Webcam.Interop;

/// <summary>Reference-counted MFStartup/MFShutdown for the process.</summary>
internal static class MediaFoundationRuntime
{
    private static readonly Lock Gate = new();
    private static int _references;

    public static IDisposable Acquire()
    {
        lock (Gate)
        {
            if (_references == 0)
            {
                PInvoke.MFStartup(PInvoke.MF_VERSION, PInvoke.MFSTARTUP_FULL).ThrowOnFailure();
            }

            _references++;
        }

        return new Releaser();
    }

    private sealed class Releaser : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
            {
                return;
            }

            lock (Gate)
            {
                if (--_references == 0)
                {
                    _ = PInvoke.MFShutdown();
                }
            }
        }
    }
}

/// <summary>Small conveniences over the generated Media Foundation bindings.</summary>
internal static class MfInterop
{
    /// <summary>MF_E_NO_MORE_TYPES, returned when a media-type index is past the end.</summary>
    public const int NoMoreTypes = unchecked((int)0xC00D36B9);

    public static IMFAttributes CreateAttributes(uint initialSize = 4)
    {
        PInvoke.MFCreateAttributes(out var attributes, initialSize).ThrowOnFailure();
        return attributes;
    }

    public static string? GetString(IMFAttributes attributes, in Guid key)
    {
        try
        {
            attributes.GetStringLength(key, out var length);
            var buffer = new char[length + 1];
            attributes.GetString(key, buffer.AsSpan());
            return new string(buffer, 0, (int)length);
        }
        catch (COMException)
        {
            return null;
        }
    }

    public static Guid? GetGuid(IMFAttributes attributes, in Guid key)
    {
        try
        {
            attributes.GetGUID(key, out var value);
            return value;
        }
        catch (COMException)
        {
            return null;
        }
    }

    public static uint? GetUInt32(IMFAttributes attributes, in Guid key)
    {
        try
        {
            attributes.GetUINT32(key, out var value);
            return value;
        }
        catch (COMException)
        {
            return null;
        }
    }

    public static ulong? GetUInt64(IMFAttributes attributes, in Guid key)
    {
        try
        {
            attributes.GetUINT64(key, out var value);
            return value;
        }
        catch (COMException)
        {
            return null;
        }
    }

    /// <summary>Splits an MF "packed" UINT64 (frame size, frame rate) into its high and low halves.</summary>
    public static (uint High, uint Low) Unpack(ulong value) => ((uint)(value >> 32), (uint)(value & 0xFFFFFFFF));

    /// <summary>Releases a runtime callable wrapper now instead of at the next GC. Media Foundation recycles a small pool
    /// of samples; holding them until finalization stalls the stream.</summary>
    public static void Release(object? comObject)
    {
        if (comObject is not null && Marshal.IsComObject(comObject))
        {
            _ = Marshal.ReleaseComObject(comObject);
        }
    }
}
