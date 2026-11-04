using System.Runtime.InteropServices;
using SoftCal.Core.Instruments;
using SoftCal.Instruments.Webcam.Interop;
using Windows.Win32;
using Windows.Win32.Devices.Properties;
using Windows.Win32.Media.KernelStreaming;
using Windows.Win32.Media.MediaFoundation;

namespace SoftCal.Instruments.Webcam.Controls;

/// <summary>State of one Windows camera effect (extended camera control).</summary>
public sealed record EffectState(CameraControl Control, bool Supported, ulong? Flags, ulong? OffFlags, string? Note = null)
{
    public bool IsOff => !Supported || Flags == OffFlags;
}

/// <summary>
/// Video HDR, background blur, eye contact and auto-framing through IMFExtendedCameraController, plus detection of
/// Windows Studio Effects. Plain UVC webcams support none of them; the lock still checks.
/// </summary>
internal static class CameraEffects
{
    private static readonly (CameraControl Control, KSPROPERTY_CAMERACONTROL_EXTENDED_PROPERTY Id, ulong Off)[] Known =
    [
        (CameraControl.VideoHdr, KSPROPERTY_CAMERACONTROL_EXTENDED_PROPERTY.KSPROPERTY_CAMERACONTROL_EXTENDED_VIDEOHDR, PInvoke.KSCAMERA_EXTENDEDPROP_VIDEOHDR_OFF),
        (CameraControl.BackgroundEffects, KSPROPERTY_CAMERACONTROL_EXTENDED_PROPERTY.KSPROPERTY_CAMERACONTROL_EXTENDED_BACKGROUNDSEGMENTATION, PInvoke.KSCAMERA_EXTENDEDPROP_BACKGROUNDSEGMENTATION_OFF),
        (CameraControl.EyeContact, KSPROPERTY_CAMERACONTROL_EXTENDED_PROPERTY.KSPROPERTY_CAMERACONTROL_EXTENDED_EYEGAZECORRECTION, PInvoke.KSCAMERA_EXTENDEDPROP_EYEGAZECORRECTION_OFF),
        (CameraControl.AutoFraming, KSPROPERTY_CAMERACONTROL_EXTENDED_PROPERTY.KSPROPERTY_CAMERACONTROL_EXTENDED_DIGITALWINDOW, PInvoke.KSCAMERA_EXTENDEDPROP_DIGITALWINDOW_MANUAL),
    ];

    public static unsafe IMFExtendedCameraController? GetController(IMFMediaSource source)
    {
        if (source is not IMFGetService service)
        {
            return null;
        }

        try
        {
            var serviceId = Guid.Empty;
            var iid = typeof(IMFExtendedCameraController).GUID;
            service.GetService(&serviceId, &iid, out var pointer);
            if (pointer is null)
            {
                return null;
            }

            // A raw interface pointer holding one reference: wrapped (the wrapper takes its own), then let go.
            try
            {
                return Marshal.GetObjectForIUnknown((nint)pointer) as IMFExtendedCameraController;
            }
            finally
            {
                Marshal.Release((nint)pointer);
            }
        }
        catch (COMException)
        {
            return null;
        }
        catch (InvalidCastException)
        {
            return null;
        }
    }

    public static IReadOnlyList<EffectState> Read(IMFExtendedCameraController? controller)
    {
        var states = new List<EffectState>();
        foreach (var (control, id, off) in Known)
        {
            states.Add(ReadOne(controller, control, id, off));
        }

        return states;
    }

    /// <summary>Turns every supported effect off; returns the states before the change, for <see cref="Restore"/>.</summary>
    public static IReadOnlyList<EffectState> TurnOff(IMFExtendedCameraController? controller)
    {
        var before = Read(controller);
        if (controller is null)
        {
            return before;
        }

        foreach (var state in before.Where(s => s.Supported && !s.IsOff))
        {
            var (_, id, off) = Known.First(k => k.Control == state.Control);
            Write(controller, id, off);
        }

        return before;
    }

    public static void Restore(IMFExtendedCameraController? controller, IReadOnlyList<EffectState> before)
    {
        if (controller is null)
        {
            return;
        }

        foreach (var state in before.Where(s => s.Supported && s.Flags is not null && !s.IsOff))
        {
            var (_, id, _) = Known.First(k => k.Control == state.Control);
            Write(controller, id, state.Flags!.Value);
        }
    }

    /// <summary>DEVPKEY_DeviceInterface_IsWindowsCameraEffectAvailable for the camera; null when unknown.</summary>
    public static unsafe bool? IsStudioEffectsAvailable(string symbolicLink)
    {
        var buffer = new byte[4];
        var size = (uint)buffer.Length;
        var result = PInvoke.CM_Get_Device_Interface_Property(
            symbolicLink,
            PInvoke.DEVPKEY_DeviceInterface_IsWindowsCameraEffectAvailable,
            out var type,
            buffer,
            ref size,
            0);
        if (result != 0 || size < 1)
        {
            return null;
        }

        // DEVPROP_TYPE_BOOLEAN: DEVPROP_TRUE is 0xFF.
        return type == DEVPROPTYPE.DEVPROP_TYPE_BOOLEAN ? buffer[0] != 0 : null;
    }

    private static EffectState ReadOne(IMFExtendedCameraController? controller, CameraControl control, KSPROPERTY_CAMERACONTROL_EXTENDED_PROPERTY id, ulong off)
    {
        if (controller is null)
        {
            return new EffectState(control, false, null, off, "no extended camera controller");
        }

        IMFExtendedCameraControl? ext = null;
        try
        {
            controller.GetExtendedCameraControl((uint)MF_CAPTURE_ENGINE_SOURCE.MF_CAPTURE_ENGINE_MEDIASOURCE, (uint)id, out ext);
            return new EffectState(control, true, ext.GetFlags(), off);
        }
        catch (COMException ex)
        {
            return new EffectState(control, false, null, off, $"not supported (0x{ex.HResult:X8})");
        }
        finally
        {
            MfInterop.Release(ext);
        }
    }

    private static void Write(IMFExtendedCameraController controller, KSPROPERTY_CAMERACONTROL_EXTENDED_PROPERTY id, ulong flags)
    {
        IMFExtendedCameraControl? ext = null;
        try
        {
            controller.GetExtendedCameraControl((uint)MF_CAPTURE_ENGINE_SOURCE.MF_CAPTURE_ENGINE_MEDIASOURCE, (uint)id, out ext);
            ext.SetFlags(flags);
            ext.CommitSettings();
        }
        finally
        {
            MfInterop.Release(ext);
        }
    }
}
