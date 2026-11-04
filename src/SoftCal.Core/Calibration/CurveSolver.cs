using SoftCal.Core.Analysis;
using SoftCal.Core.Measurement;
using SoftCal.Core.ToneCurves;

namespace SoftCal.Core.Calibration;

/// <summary>The tone the calibration curves aim for.</summary>
public enum ToneTarget
{
    /// <summary>Keep the display's own tone curve; only the gray balance is corrected.</summary>
    KeepNative,

    /// <summary>A pure power law (<see cref="CalibrationTarget.Gamma"/>).</summary>
    Gamma,

    /// <summary>The sRGB piecewise curve.</summary>
    Srgb,
}

/// <param name="NeutralGrays">Correct the gray balance (each channel follows the target on its own), or only the tone
/// (every channel gets the same correction, so the grays keep their tint).</param>
public sealed record CalibrationTarget(ToneTarget Tone = ToneTarget.Gamma, double Gamma = 2.2, bool NeutralGrays = true)
{
    /// <summary>The target's share of the black-to-white light at frame-buffer code <paramref name="code"/> (0–255);
    /// <paramref name="native"/> is the display's own luma tone, for <see cref="ToneTarget.KeepNative"/>.</summary>
    public double Fraction(double code, Func<double, double> native)
    {
        ArgumentNullException.ThrowIfNull(native);
        var v = Math.Clamp(code / 255, 0, 1);
        return Tone switch
        {
            ToneTarget.KeepNative => native(code),
            ToneTarget.Srgb => SrgbCurve.Instance.ToLinear(v),
            _ => Math.Pow(v, Gamma),
        };
    }
}

/// <summary>
/// The GPU calibration curves: for each channel, the output code (0–255, fractional) sent for each of the 256
/// frame-buffer codes. Native white is kept: code 255 leaves as 255 in every channel.
/// </summary>
/// <param name="AnchorCrush">The display's own black crush the curves were anchored to (codes up to it give no light;
/// input 1 leaves just above it). A refine pass keeps it: measured through the curves, the near-black steps show the
/// curves' own dark start, not the display's.</param>
public sealed record CalibrationCurves(IReadOnlyList<double> Red, IReadOnlyList<double> Green, IReadOnlyList<double> Blue, int AnchorCrush = 0)
{
    public static CalibrationCurves Identity { get; } = new(Linear(), Linear(), Linear());

    public IReadOnlyList<double> this[int channel] => channel switch
    {
        0 => Red,
        1 => Green,
        2 => Blue,
        _ => throw new ArgumentOutOfRangeException(nameof(channel), channel, "Channel must be 0, 1 or 2."),
    };

    /// <summary>The output for a fractional input code (linear between the 256 entries).</summary>
    public double OutputAt(int channel, double input)
    {
        var curve = this[channel];
        var x = Math.Clamp(input, 0, 255);
        var lower = Math.Min((int)x, 254);
        return curve[lower] + ((curve[lower + 1] - curve[lower]) * (x - lower));
    }

    /// <summary>The 16-bit GPU ramp (what <c>SetDeviceGammaRamp</c> and the profile's 'vcgt' hold).</summary>
    public ushort[] Ramp(int channel) => [.. this[channel].Select(v => (ushort)Math.Round(Math.Clamp(v / 255, 0, 1) * 65535))];

    /// <summary>How many of the 256 levels stay distinct once the output is rounded to 8 bits (the panel's input).</summary>
    public int LevelsKept(int channel) => this[channel].Select(v => (int)Math.Round(Math.Clamp(v, 0, 255))).Distinct().Count();

    private static double[] Linear() => [.. Enumerable.Range(0, 256).Select(i => (double)i)];
}

/// <summary>
/// Solves calibration curves from a measurement (plan §7.1). The gray tree gives each channel's response at about eight
/// grays, through whatever curves were loaded while measuring (<c>current</c>); for every frame-buffer code the solver
/// finds the input at which each channel delivers the target's light, and sends what the current curves send for that
/// input. Measuring again with the result and solving again refines it (the panel's channels interact a little).
/// The display's own black and white are kept, so the black keeps its tint and no channel is crushed or clipped.
/// </summary>
public static class CurveSolver
{
    public static CalibrationCurves Solve(DisplayMeasurement measurement, CalibrationTarget target, CalibrationCurves? current = null)
    {
        ArgumentNullException.ThrowIfNull(measurement);
        ArgumentNullException.ThrowIfNull(target);
        var refining = current is not null && current != CalibrationCurves.Identity;
        current ??= CalibrationCurves.Identity;
        var response = DisplayAnalysis.ChannelResponse(measurement.GrayTree);
        if (response.Count < 2)
        {
            throw new ArgumentException("The measurement needs at least two gray-tree levels with each channel's match.", nameof(measurement));
        }

        // Codes up to the crush give no light at all: anchor the curves there rather than at 0, so the solver does
        // not spend output levels on them. Measured without curves, the crush is the display's own (from the
        // near-black steps). A refine pass measures through the current curves, whose near-black steps show the
        // curves' own dark start, coarsely (codes 0, 4, 6, 8, 12, 16, 20): on 2026-10-18 the trial reported codes
        // 0–20 as black, and the refined curve sent inputs 1–12 to codes 10.8–11.7, two levels on the 8-bit link.
        // The display's own crush travels with the curves instead; the refine anchors each channel at the input where
        // the current curves reach it (pass 1 sends input 1 just above the crush, so this lies just below input 1).
        var nativeCrush = refining ? current.AnchorCrush : ChannelCurve.Crush(measurement);
        var crushes = Enumerable.Range(0, 3).Select(c => refining ? InputReaching(current[c], nativeCrush) : nativeCrush).ToArray();
        var luma = new ChannelCurve(response.Select(p => (p.Code, p.Fraction)), crushes.Min());
        double Native(double code) => luma.Evaluate(code);
        var curves = new double[3][];
        for (var c = 0; c < 3; c++)
        {
            var channel = c;
            var own = target.NeutralGrays ? new ChannelCurve(response.Select(p => (p.Code, p.ChannelFraction[channel])), crushes[channel]) : luma;
            var outputs = new double[256];
            for (var code = 0; code < 256; code++)
            {
                var wanted = target.Fraction(code, Native);
                var input = code == 0 ? 0 : code == 255 ? 255 : own.Inverse(wanted);
                outputs[code] = current.OutputAt(channel, input);
            }

            // Monotone, with the display's own black and white.
            outputs[0] = current[channel][0];
            outputs[255] = current[channel][255];
            for (var code = 1; code < 256; code++)
            {
                outputs[code] = Math.Max(outputs[code], outputs[code - 1]);
            }

            curves[c] = outputs;
        }

        return new CalibrationCurves(curves[0], curves[1], curves[2], nativeCrush);
    }

    /// <summary>The (fractional) input at which the curve's output reaches <paramref name="code"/>.</summary>
    private static double InputReaching(IReadOnlyList<double> curve, double code)
    {
        for (var i = 1; i < curve.Count; i++)
        {
            if (curve[i] > code)
            {
                return i - 1 + ((code - curve[i - 1]) / (curve[i] - curve[i - 1]));
            }
        }

        return curve.Count - 1;
    }
}
