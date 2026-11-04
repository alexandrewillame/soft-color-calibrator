using SoftCal.Core.Color;
using SoftCal.Core.Measurement;

namespace SoftCal.Instruments.Simulated;

/// <summary>How the GPU's RGB range and the monitor's expected range disagree.</summary>
public enum RangeMismatch
{
    None,

    /// <summary>GPU sends full range (0–255) but the monitor expects limited (16–235): crushed blacks, clipped whites.</summary>
    FullSentLimitedExpected,

    /// <summary>GPU sends limited range but the monitor expects full: raised blacks, dim whites.</summary>
    LimitedSentFullExpected,
}

/// <summary>Ground-truth behaviour of the simulated display. Every effect can be switched on independently.</summary>
public sealed record SimulatedDisplayOptions
{
    /// <summary>Per-channel exponent of the panel's native response.</summary>
    public Vec3 Gamma { get; init; } = new(2.2, 2.2, 2.2);

    /// <summary>Linear output of each channel at full drive, above black (sets the native white balance).</summary>
    public Vec3 ChannelGain { get; init; } = Vec3.One;

    /// <summary>Light emitted per channel at code 0 (backlight leakage; may be tinted).</summary>
    public Vec3 Black { get; init; } = new(0.0010, 0.0010, 0.0013);

    public RangeMismatch RangeMismatch { get; init; } = RangeMismatch.None;

    /// <summary>Codes at or above this value give full output (white clipping, e.g. contrast set too high).</summary>
    public double ClipCode { get; init; } = 255;

    /// <summary>Codes at or below this value give black (black crush, e.g. brightness set too low).</summary>
    public double CrushCode { get; init; }

    /// <summary>0 = spatially additive panel. Positive values bias halftones toward the output of the averaged code.</summary>
    public double HalftoneCrosstalk { get; init; }

    /// <summary>Bits sent to the panel after the GPU LUT (no dithering).</summary>
    public int OutputBits { get; init; } = 8;

    /// <summary>
    /// 0 = channels independent. Otherwise each channel's light is scaled by 1 + k × the average drive (sent code ÷
    /// 255) of the two other channels, per channel. Colors shown alone are unaffected while grays change, as on the
    /// BenQ XL2720 (TN), whose single-color half-light codes sit 18 codes from its gray one.
    /// </summary>
    public Vec3 ChannelInteraction { get; init; }

    /// <summary>
    /// 0 = even code steps. Otherwise each channel's code wobbles by ± this many codes with a 4-code period, a third of
    /// a period apart between channels, so single-code steps are uneven and differ per channel as on 6-bit+FRC panels
    /// (the BenQ XL2720's blue stepped 1.6 where its neighbours averaged 2.6). Must stay below 0.6 to keep steps rising.
    /// </summary>
    public double StepUnevenness { get; init; }

    /// <summary>The monitor's on-screen controls; null = a fixed monitor (the default).
    /// Their current values are in <see cref="SimulatedDisplay.Osd"/>.</summary>
    public SimulatedOsdOptions? Controls { get; init; }
}

/// <summary>A display with a known response, including the GPU calibration LUT (vcgt) stage in front of it.</summary>
public sealed class SimulatedDisplay
{
    private readonly ushort[][] _lut = [IdentityRamp(), IdentityRamp(), IdentityRamp()];

    public SimulatedDisplay(SimulatedDisplayOptions? options = null)
    {
        Options = options ?? new SimulatedDisplayOptions();
        if (Options.ClipCode <= Options.CrushCode)
        {
            throw new ArgumentException("ClipCode must be above CrushCode.", nameof(options));
        }

        if (Math.Abs(Options.StepUnevenness) >= 0.6)
        {
            throw new ArgumentException("StepUnevenness must stay below 0.6 codes, or some steps would go down.", nameof(options));
        }
    }

    public SimulatedDisplayOptions Options { get; }

    /// <summary>The OSD's current values; they act only when <see cref="SimulatedDisplayOptions.Controls"/> is set.</summary>
    public SimulatedOsd Osd { get; } = new();

    public IReadOnlyList<ushort> GetGpuLut(int channel) => _lut[channel];

    /// <summary>Loads a GPU gamma ramp: 3 channels × 256 entries of 16-bit values.</summary>
    public void SetGpuLut(IReadOnlyList<IReadOnlyList<ushort>> lut)
    {
        ArgumentNullException.ThrowIfNull(lut);
        if (lut.Count != 3 || lut.Any(c => c.Count != 256))
        {
            throw new ArgumentException("A gamma ramp has 3 channels of 256 entries.", nameof(lut));
        }

        for (var c = 0; c < 3; c++)
        {
            for (var i = 0; i < 256; i++)
            {
                _lut[c][i] = lut[c][i];
            }
        }
    }

    public void ResetGpuLut()
    {
        for (var c = 0; c < 3; c++)
        {
            _lut[c] = IdentityRamp();
        }
    }

    /// <summary>Light emitted for a patch, per display channel (black + gain × response).</summary>
    public Vec3 Emit(PatchSpec patch)
    {
        ArgumentNullException.ThrowIfNull(patch);
        return patch switch
        {
            SolidPatch solid => Emit(solid.Color),
            HalftonePatch halftone => EmitHalftone(halftone),

            // Thin lines on black: next to nothing reaches the camera's region.
            PlacementTargetPatch => Emit(DeviceRgb.Black),
            _ => throw new NotSupportedException($"Unsupported patch type {patch.GetType().Name}."),
        };
    }

    public Vec3 Emit(DeviceRgb color)
    {
        var sent = new Vec3(Sent(0, color.R), Sent(1, color.G), Sent(2, color.B));
        var light = new Vec3(ChannelResponse(0, sent.X), ChannelResponse(1, sent.Y), ChannelResponse(2, sent.Z));
        if (Options.ChannelInteraction == Vec3.Zero)
        {
            return light;
        }

        var drive = sent / 255;
        var others = new Vec3((drive.Y + drive.Z) / 2, (drive.X + drive.Z) / 2, (drive.X + drive.Y) / 2);
        return light.Scale(Vec3.One + Options.ChannelInteraction.Scale(others));
    }

    /// <summary>
    /// Ground truth: light emitted by one channel when the panel receives <paramref name="sentCode"/> (0–255, may be
    /// fractional), i.e. after the GPU LUT but before the monitor's own processing.
    /// </summary>
    public double ChannelResponse(int channel, double sentCode)
    {
        var code = Options.RangeMismatch switch
        {
            RangeMismatch.FullSentLimitedExpected => (sentCode - 16) * 255.0 / 219.0,
            RangeMismatch.LimitedSentFullExpected => 16 + (sentCode * 219.0 / 255.0),
            _ => sentCode,
        };
        if (Options.StepUnevenness != 0)
        {
            code += Options.StepUnevenness * Math.Sin((2 * Math.PI * code / 4) + (2 * Math.PI * channel / 3));
        }

        var gamma = Options.Gamma[channel];
        var backlight = 1.0;
        if (Options.Controls is { } controls)
        {
            (code, gamma, backlight) = ApplyControls(controls, channel, code, gamma);
        }

        var v = Math.Clamp((code - Options.CrushCode) / (Options.ClipCode - Options.CrushCode), 0, 1);
        return backlight * (Options.Black[channel] + (Options.ChannelGain[channel] * Math.Pow(v, gamma)));
    }

    /// <summary>
    /// The monitor's own processing, in the usual order: RGB gain and contrast scale the signal (a gain above 1 clips the
    /// top codes), black boost lifts the codes near black, an offset brightness shifts everything, the gamma preset
    /// replaces the panel's exponent (keeping the channels' differences), and a backlight brightness scales the light
    /// (as do the RGB gains on a monitor whose gains scale the light).
    /// </summary>
    private (double Code, double Gamma, double Backlight) ApplyControls(SimulatedOsdOptions controls, int channel, double code, double gamma)
    {
        var osd = Osd;
        var gain = osd.EffectiveGain(channel) / 100.0;
        if (!controls.GainsScaleLight)
        {
            code *= Math.Pow(gain, controls.GainExponent);
        }

        code *= 1 + ((osd.Contrast - controls.NeutralContrast) * controls.GainPerContrastStep);
        if (controls.BoostPerStep > 0 && osd.BlackBoost > 0 && code is > 0 and < 64)
        {
            code += Math.Min(osd.BlackBoost, controls.MaxBoost) * controls.BoostPerStep * Math.Min(1, code / 2) * (1 - (code / 64));
        }

        var backlight = 1.0;
        if (controls.BrightnessKind == BrightnessKind.Offset)
        {
            code += (osd.Brightness - controls.NeutralBrightness) * controls.OffsetPerStep;
        }
        else
        {
            backlight = 0.1 + (0.9 * Math.Clamp(osd.Brightness, 0, 100) / 100.0);
        }

        if (controls.GammaPresets.Count > 0)
        {
            gamma *= controls.GammaPresets[Math.Clamp(osd.GammaPreset, 0, controls.GammaPresets.Count - 1)] / 2.2;
        }

        return (code, gamma, controls.GainsScaleLight ? backlight * gain : backlight);
    }

    /// <summary>The code the panel receives for a frame-buffer code, after the GPU LUT and the link's bit depth.</summary>
    private double Sent(int channel, byte code)
    {
        var levels = (1 << Options.OutputBits) - 1;
        return Math.Round(_lut[channel][code] / 65535.0 * levels) / levels * 255.0;
    }

    private Vec3 EmitHalftone(HalftonePatch halftone)
    {
        var f = halftone.OnFraction;
        var mean = (Emit(halftone.On) * f) + (Emit(halftone.Off) * (1 - f));
        if (Options.HalftoneCrosstalk == 0)
        {
            return mean;
        }

        byte Mix(byte on, byte off) => (byte)Math.Round((on * f) + (off * (1 - f)));
        var averaged = Emit(new DeviceRgb(
            Mix(halftone.On.R, halftone.Off.R),
            Mix(halftone.On.G, halftone.Off.G),
            Mix(halftone.On.B, halftone.Off.B)));
        return mean + ((averaged - mean) * Options.HalftoneCrosstalk);
    }

    private static ushort[] IdentityRamp()
    {
        var ramp = new ushort[256];
        for (var i = 0; i < 256; i++)
        {
            ramp[i] = (ushort)(i * 257);
        }

        return ramp;
    }
}
