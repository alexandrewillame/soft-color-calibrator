using System.Globalization;
using SoftCal.Core.Calibration;
using SoftCal.Core.Color;
using SoftCal.Core.Measurement;

namespace SoftCal.Icc;

/// <summary>
/// The profile SoftCal installs for a calibrated display: its primaries and white from the EDID (the camera does not
/// measure absolute color; the native white is kept), tone curves measured with the calibration curves loaded (so the
/// profile describes the display as color-managed apps will see it), and those curves in 'vcgt' for Windows' loader.
/// Without curves it is a profile-only description of the display as measured.
/// </summary>
public static class CalibrationProfile
{
    public static IccProfile Create(string description, RgbPrimaries primaries, DisplayMeasurement measuredWithCurves, CalibrationCurves? curves, DateTime createdUtc)
    {
        ArgumentNullException.ThrowIfNull(measuredWithCurves);
        var tone = ChannelCurve.Measured(measuredWithCurves).Select(c => (IReadOnlyList<double>)c.Sample()).ToList();
        var vcgt = curves is null ? null : new VideoCardGamma(curves.Ramp(0), curves.Ramp(1), curves.Ramp(2));
        return IccProfile.ForDisplay(description, primaries, tone, vcgt, createdUtc);
    }

    /// <summary>"SoftCal ZOWIE XL LCD Desk 2026-10-17 2115.icc" (display, SoftCal profile, time): unique per run, so
    /// installing never overwrites.</summary>
    public static string FileName(string displayName, string? profile, DateTime local)
    {
        static string Clean(string s) => string.Concat(s.Select(c => char.IsAsciiLetterOrDigit(c) || c is ' ' or '-' or '_' ? c : '_')).Trim();
        var name = string.IsNullOrWhiteSpace(profile) ? string.Empty : " " + Clean(profile);
        return string.Create(CultureInfo.InvariantCulture, $"SoftCal {Clean(displayName)}{name} {local:yyyy-MM-dd HHmm}.icc");
    }
}
