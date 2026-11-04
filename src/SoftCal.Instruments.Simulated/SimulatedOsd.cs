namespace SoftCal.Instruments.Simulated;

/// <summary>What a monitor's Brightness control does to the picture.</summary>
public enum BrightnessKind
{
    /// <summary>Scales the backlight: every level, black included, changes by the same factor (most LCDs).</summary>
    Backlight,

    /// <summary>Shifts the signal: raising it lifts black, lowering it crushes the shadows.</summary>
    Offset,
}

/// <summary>How a simulated monitor's on-screen (OSD) controls act on the picture.</summary>
public sealed record SimulatedOsdOptions
{
    public BrightnessKind BrightnessKind { get; init; } = BrightnessKind.Backlight;

    /// <summary>Offset brightness: the setting that neither lifts nor crushes.</summary>
    public int NeutralBrightness { get; init; } = 50;

    /// <summary>Offset brightness: codes added per step above <see cref="NeutralBrightness"/>.</summary>
    public double OffsetPerStep { get; init; } = 0.4;

    /// <summary>The contrast setting that passes the signal unchanged.</summary>
    public int NeutralContrast { get; init; } = 50;

    /// <summary>Signal gain per contrast step above neutral (0.01 = +1 %); a gain above 1 clips the top codes.</summary>
    public double GainPerContrastStep { get; init; } = 0.01;

    /// <summary>Each gamma preset's exponent; empty when the monitor has no gamma control.</summary>
    public IReadOnlyList<double> GammaPresets { get; init; } = [];

    /// <summary>Black boost (e.g. BenQ's Black eQualizer): codes lifted near black per step; 0 = no such control.</summary>
    public double BoostPerStep { get; init; }

    /// <summary>False (most monitors): the RGB gains scale the signal, like Contrast. True: they scale each channel's
    /// light after the panel's response, so a gain step moves the white about 2.2 times less.</summary>
    public bool GainsScaleLight { get; init; }

    /// <summary>When the gains scale the signal: by gain^this. 1 = in proportion; below 1 = more gently, as on the BenQ
    /// XL2720, whose gain steps moved its white about 0.7 times as much as the same steps of the signal.</summary>
    public double GainExponent { get; init; } = 1;

    public int MaxBoost { get; init; } = 20;
}

/// <summary>The simulated monitor's current OSD values: what the user (or DDC/CI) changes.</summary>
public sealed class SimulatedOsd
{
    public int Brightness { get; set; } = 50;

    public int Contrast { get; set; } = 50;

    /// <summary>RGB gains in percent; 100 passes the channel unchanged (many monitors cannot go above). They act in the
    /// user color mode; a factory preset uses its own <see cref="PresetGains"/>.</summary>
    public int RedGain { get; set; } = 100;

    public int GreenGain { get; set; } = 100;

    public int BlueGain { get; set; } = 100;

    /// <summary>The color preset's own gains (a factory preset such as 6500 K, whose values a firmware update may have
    /// reset), used while <see cref="UserColorMode"/> is off; null = the preset passes every channel unchanged.</summary>
    public (int Red, int Green, int Blue)? PresetGains { get; set; }

    /// <summary>True when the user color mode is chosen, so <see cref="RedGain"/>, … act (the default: a monitor with
    /// only these gains).</summary>
    public bool UserColorMode { get; set; } = true;

    /// <summary>Index into <see cref="SimulatedOsdOptions.GammaPresets"/>.</summary>
    public int GammaPreset { get; set; }

    public int BlackBoost { get; set; }

    public int Gain(int channel) => channel switch
    {
        0 => RedGain,
        1 => GreenGain,
        _ => BlueGain,
    };

    /// <summary>The gain that acts on a channel now: the user mode's, or the preset's.</summary>
    public int EffectiveGain(int channel) => UserColorMode || PresetGains is not { } preset
        ? Gain(channel)
        : channel switch
        {
            0 => preset.Red,
            1 => preset.Green,
            _ => preset.Blue,
        };
}
