using System.Globalization;

namespace SoftCal.Core.Advisor;

/// <summary>The monitor's on-screen (OSD) controls the advisor uses.</summary>
/// <remarks>RGB gains come with the white-point estimate: the default white target is the monitor's native white, which
/// needs no gain change.</remarks>
public enum OsdControlId
{
    Brightness,
    Contrast,
    GammaPreset,

    /// <summary>A shadow lift such as BenQ's Black eQualizer or a "dark boost".</summary>
    BlackBoost,
}

/// <summary>One control the monitor has: a numeric range, or presets (the value is then the preset's index).</summary>
public sealed record OsdControl(OsdControlId Id, string Name, int Min, int Max, IReadOnlyList<string>? Presets = null)
{
    public bool IsPreset => Presets is { Count: > 0 };

    /// <summary>A first move when searching: a tenth of the range, at least 2.</summary>
    public int SearchStep => Math.Max(2, (Max - Min) / 10);

    public string Describe(int value) =>
        IsPreset && value >= 0 && value < Presets!.Count ? Presets[value] : value.ToString(CultureInfo.InvariantCulture);

    public int Clamp(int value) => Math.Clamp(value, Min, IsPreset ? Presets!.Count - 1 : Max);
}

/// <summary>The controls one monitor has (saved per display, from the questionnaire or DDC/CI).</summary>
public sealed record MonitorControlProfile(IReadOnlyList<OsdControl> Controls)
{
    public OsdControl? Find(OsdControlId id) => Controls.FirstOrDefault(c => c.Id == id);
}

/// <summary>What the advisor aims for.</summary>
/// <param name="Gamma">The tone target for choosing a gamma preset.</param>
/// <param name="ChooseGammaPreset">False skips the gamma presets (gamma advice is optional).</param>
public sealed record AdvisorTargets(double Gamma = 2.2, bool ChooseGammaPreset = true);
