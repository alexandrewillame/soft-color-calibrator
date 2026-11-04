using SoftCal.Core.Instruments;

namespace SoftCal.Instruments.Webcam.Controls;

/// <summary>The two UVC property sets exposed by Windows' camera driver.</summary>
public enum UvcControlSet
{
    /// <summary>PROPSETID_VIDCAP_VIDEOPROCAMP (IAMVideoProcAmp for ids 0–9).</summary>
    VideoProcAmp,

    /// <summary>PROPSETID_VIDCAP_CAMERACONTROL (IAMCameraControl for ids 0–6).</summary>
    CameraControl,
}

/// <summary>One UVC control: its property set and KSPROPERTY id.</summary>
public readonly record struct UvcControlId(UvcControlSet Set, int Property)
{
    /// <summary>True when the control is reached through IAMVideoProcAmp / IAMCameraControl rather than IKsControl.</summary>
    public bool HasDirectShowInterface => Set == UvcControlSet.VideoProcAmp ? Property <= 9 : Property <= 6;

    public override string ToString() => UvcControlCatalog.NameOf(this);
}

/// <summary>A control's range as reported by GetRange.</summary>
public sealed record UvcControlRange(int Min, int Max, int Step, int Default, bool SupportsAuto, bool SupportsManual)
{
    public int Clamp(int value)
    {
        var clamped = Math.Clamp(value, Min, Max);
        if (Step > 1)
        {
            clamped = Min + ((int)Math.Round((clamped - Min) / (double)Step) * Step);
            clamped = Math.Clamp(clamped, Min, Max);
        }

        return clamped;
    }
}

/// <summary>A control's value and mode.</summary>
public readonly record struct UvcControlValue(int Value, bool IsAuto);

/// <summary>Reads and writes UVC controls. The Media Foundation implementation talks to the camera; tests use a fake.</summary>
public interface IUvcControlBackend
{
    /// <summary>Null when the camera does not support the control or its range cannot be read.</summary>
    UvcControlRange? GetRange(UvcControlId id);

    /// <summary>Null when the value cannot be read.</summary>
    UvcControlValue? Get(UvcControlId id);

    /// <summary>Writes the control; throws when the camera rejects the write.</summary>
    void Set(UvcControlId id, UvcControlValue value);
}

/// <summary>Every control the lock touches, with its role.</summary>
public static class UvcControlCatalog
{
    public static readonly UvcControlId Brightness = new(UvcControlSet.VideoProcAmp, 0);
    public static readonly UvcControlId Contrast = new(UvcControlSet.VideoProcAmp, 1);
    public static readonly UvcControlId Hue = new(UvcControlSet.VideoProcAmp, 2);
    public static readonly UvcControlId Saturation = new(UvcControlSet.VideoProcAmp, 3);
    public static readonly UvcControlId Sharpness = new(UvcControlSet.VideoProcAmp, 4);
    public static readonly UvcControlId Gamma = new(UvcControlSet.VideoProcAmp, 5);
    public static readonly UvcControlId ColorEnable = new(UvcControlSet.VideoProcAmp, 6);
    public static readonly UvcControlId WhiteBalance = new(UvcControlSet.VideoProcAmp, 7);
    public static readonly UvcControlId BacklightCompensation = new(UvcControlSet.VideoProcAmp, 8);
    public static readonly UvcControlId Gain = new(UvcControlSet.VideoProcAmp, 9);
    public static readonly UvcControlId PowerLineFrequency = new(UvcControlSet.VideoProcAmp, 13);

    public static readonly UvcControlId Pan = new(UvcControlSet.CameraControl, 0);
    public static readonly UvcControlId Tilt = new(UvcControlSet.CameraControl, 1);
    public static readonly UvcControlId Roll = new(UvcControlSet.CameraControl, 2);
    public static readonly UvcControlId Zoom = new(UvcControlSet.CameraControl, 3);
    public static readonly UvcControlId Exposure = new(UvcControlSet.CameraControl, 4);
    public static readonly UvcControlId Iris = new(UvcControlSet.CameraControl, 5);
    public static readonly UvcControlId Focus = new(UvcControlSet.CameraControl, 6);
    public static readonly UvcControlId Privacy = new(UvcControlSet.CameraControl, 8);
    public static readonly UvcControlId AutoExposurePriority = new(UvcControlSet.CameraControl, 19);

    /// <summary>All controls that are snapshotted and restored, in the order they are written.</summary>
    public static IReadOnlyList<UvcControlId> All { get; } =
    [
        WhiteBalance, Exposure, Gain, BacklightCompensation, Sharpness, Focus,
        Brightness, Contrast, Hue, Saturation, Gamma, ColorEnable,
        Pan, Tilt, Roll, Zoom, Iris,
        PowerLineFrequency, AutoExposurePriority,
    ];

    /// <summary>Controls that are only ever read (writing Privacy could close a privacy shutter).</summary>
    public static IReadOnlyList<UvcControlId> ReadOnly { get; } = [Privacy];

    public static string NameOf(UvcControlId id) => id.Set switch
    {
        UvcControlSet.VideoProcAmp => id.Property switch
        {
            0 => "Brightness",
            1 => "Contrast",
            2 => "Hue",
            3 => "Saturation",
            4 => "Sharpness",
            5 => "Gamma",
            6 => "ColorEnable",
            7 => "WhiteBalance",
            8 => "BacklightCompensation",
            9 => "Gain",
            10 => "DigitalMultiplier",
            11 => "DigitalMultiplierLimit",
            13 => "PowerLineFrequency",
            _ => $"VideoProcAmp{id.Property}",
        },
        _ => id.Property switch
        {
            0 => "Pan",
            1 => "Tilt",
            2 => "Roll",
            3 => "Zoom",
            4 => "Exposure",
            5 => "Iris",
            6 => "Focus",
            7 => "ScanMode",
            8 => "Privacy",
            19 => "AutoExposurePriority",
            _ => $"CameraControl{id.Property}",
        },
    };

    /// <summary>The platform-neutral control a UVC control corresponds to, if any.</summary>
    public static CameraControl? ToCameraControl(UvcControlId id)
    {
        if (id == WhiteBalance)
        {
            return CameraControl.WhiteBalance;
        }

        if (id == Exposure)
        {
            return CameraControl.Exposure;
        }

        if (id == Gain)
        {
            return CameraControl.Gain;
        }

        if (id == Focus)
        {
            return CameraControl.Focus;
        }

        if (id == BacklightCompensation)
        {
            return CameraControl.BacklightCompensation;
        }

        if (id == Sharpness)
        {
            return CameraControl.Sharpness;
        }

        if (id == Saturation)
        {
            return CameraControl.Saturation;
        }

        if (id == Contrast)
        {
            return CameraControl.Contrast;
        }

        if (id == Brightness)
        {
            return CameraControl.Brightness;
        }

        if (id == Gamma)
        {
            return CameraControl.Gamma;
        }

        if (id == Hue)
        {
            return CameraControl.Hue;
        }

        if (id == PowerLineFrequency)
        {
            return CameraControl.PowerLineFrequency;
        }

        return null;
    }
}
