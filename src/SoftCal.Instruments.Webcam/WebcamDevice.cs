using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using SoftCal.Instruments.Webcam.Interop;
using Windows.Win32;
using Windows.Win32.Media.MediaFoundation;

namespace SoftCal.Instruments.Webcam;

/// <summary>A video capture device as Media Foundation lists it.</summary>
public sealed partial record WebcamDevice(string FriendlyName, string SymbolicLink)
{
    /// <summary>USB vendor ID, when the symbolic link is a USB hardware path.</summary>
    public int? VendorId => Parse().Vid;

    public int? ProductId => Parse().Pid;

    /// <summary>True for a physical USB camera. Virtual cameras (software-device or root-enumerated links) are excluded
    /// from measurements because they may process or synthesize frames.</summary>
    public bool IsUsbHardware => Parse().Vid is not null;

    /// <summary>A stable, privacy-preserving identifier: VID:PID plus the interface number, without the instance path.</summary>
    public string StableId => IsUsbHardware
        ? string.Create(CultureInfo.InvariantCulture, $"usb-{VendorId:x4}-{ProductId:x4}")
        : "virtual-" + FriendlyName.ToLowerInvariant().Replace(' ', '-');

    /// <summary>Lists video capture devices; physical USB cameras first.</summary>
    public static unsafe IReadOnlyList<WebcamDevice> Enumerate()
    {
        using var mf = MediaFoundationRuntime.Acquire();
        var attributes = MfInterop.CreateAttributes(1);
        try
        {
            attributes.SetGUID(PInvoke.MF_DEVSOURCE_ATTRIBUTE_SOURCE_TYPE, PInvoke.MF_DEVSOURCE_ATTRIBUTE_SOURCE_TYPE_VIDCAP_GUID);
            PInvoke.MFEnumDeviceSources(attributes, out var activates, out var count).ThrowOnFailure();
            var devices = new List<WebcamDevice>((int)count);
            try
            {
                for (var i = 0; i < count; i++)
                {
                    var activate = (IMFActivate)Marshal.GetObjectForIUnknown((nint)activates[i]);
                    try
                    {
                        var name = MfInterop.GetString(activate, PInvoke.MF_DEVSOURCE_ATTRIBUTE_FRIENDLY_NAME) ?? "(unnamed camera)";
                        var link = MfInterop.GetString(activate, PInvoke.MF_DEVSOURCE_ATTRIBUTE_SOURCE_TYPE_VIDCAP_SYMBOLIC_LINK);
                        if (link is not null)
                        {
                            devices.Add(new WebcamDevice(name, link));
                        }
                    }
                    finally
                    {
                        MfInterop.Release(activate);
                    }
                }
            }
            finally
            {
                for (var i = 0; i < count; i++)
                {
                    activates[i]->Release();
                }

                PInvoke.CoTaskMemFree(activates);
            }

            return [.. devices.OrderByDescending(d => d.IsUsbHardware).ThenBy(d => d.FriendlyName, StringComparer.Ordinal)];
        }
        finally
        {
            MfInterop.Release(attributes);
        }
    }

    private (int? Vid, int? Pid) Parse()
    {
        var match = UsbPath().Match(SymbolicLink);
        return match.Success
            ? (int.Parse(match.Groups[1].Value, NumberStyles.HexNumber, CultureInfo.InvariantCulture),
                int.Parse(match.Groups[2].Value, NumberStyles.HexNumber, CultureInfo.InvariantCulture))
            : (null, null);
    }

    [GeneratedRegex(@"^\\\\\?\\usb#vid_([0-9a-f]{4})&pid_([0-9a-f]{4})", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex UsbPath();
}
