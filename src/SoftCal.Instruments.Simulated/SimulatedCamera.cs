using SoftCal.Core.Color;
using SoftCal.Core.Instruments;
using SoftCal.Core.ToneCurves;

namespace SoftCal.Instruments.Simulated;

/// <summary>The tone curve a simulated camera applies before encoding (unknown to the measurement code).</summary>
public abstract class CameraResponse
{
    public static CameraResponse Srgb { get; } = new SrgbResponse();

    /// <summary>encoded = linear^(1/gamma).</summary>
    public static CameraResponse Power(double gamma) => new PowerResponse(gamma);

    /// <summary>No curve at all, as a phone's raw sensor data.</summary>
    public static CameraResponse Linear { get; } = new PowerResponse(1);

    /// <summary>The camera reports values proportional to the light (it says so, as a phone measuring from raw data does).</summary>
    public virtual bool IsLinear => false;

    /// <summary>sRGB followed by an S-shaped contrast boost, like many webcams' "vivid" processing.</summary>
    public static CameraResponse Filmic(double contrast) => new FilmicResponse(contrast);

    /// <summary>Maps linear sensor signal in [0, 1] to an encoded value in [0, 1].</summary>
    public abstract double Encode(double linear);

    private sealed class SrgbResponse : CameraResponse
    {
        public override double Encode(double linear) => SrgbCurve.Instance.ToEncoded(linear);
    }

    private sealed class PowerResponse(double gamma) : CameraResponse
    {
        public override bool IsLinear => gamma == 1;

        public override double Encode(double linear) => Math.Pow(Math.Clamp(linear, 0, 1), 1 / gamma);
    }

    private sealed class FilmicResponse(double contrast) : CameraResponse
    {
        public override double Encode(double linear)
        {
            var x = SrgbCurve.Instance.ToEncoded(linear);
            return 0.5 + (0.5 * Math.Tanh(contrast * (x - 0.5)) / Math.Tanh(contrast * 0.5));
        }
    }
}

/// <summary>Ground-truth behaviour of the simulated camera.</summary>
public sealed record SimulatedCameraOptions
{
    /// <summary>Sensor response to each display channel (the camera's spectral sensitivities seen through the display's primaries).</summary>
    public Matrix3 SpectralMix { get; init; } = new(
        0.45, 0.09, 0.01,
        0.10, 0.82, 0.08,
        0.02, 0.12, 0.56);

    /// <summary>White-balance gains (locked). The defaults make this display's white neutral.</summary>
    public Vec3 WhiteBalanceGains { get; init; } = new(1 / 0.55, 1.0, 1 / 0.70);

    /// <summary>The camera's color-correction matrix; rows sum to 1 so neutrals stay neutral.</summary>
    public Matrix3 ColorCorrection { get; init; } = new(
        1.55, -0.40, -0.15,
        -0.25, 1.45, -0.20,
        -0.05, -0.45, 1.50);

    public CameraResponse Response { get; init; } = CameraResponse.Srgb;

    /// <summary>Linear signal (full scale = 1) produced by display output 1.0 over one second of exposure.</summary>
    public double Sensitivity { get; init; } = 25.0;

    /// <summary>Available exposures as powers of two seconds, like Windows' UVC exposure control.</summary>
    public IReadOnlyList<int> ExposureLog2Steps { get; init; } = [-11, -10, -9, -8, -7, -6, -5, -4, -3, -2];

    /// <summary>Relative error between the actual and nominal exposure, alternating in sign from step to step.</summary>
    public double ExposureRatioError { get; init; }

    /// <summary>Per-pixel read noise, in 8-bit code units.</summary>
    public double ReadNoise { get; init; } = 0.8;

    /// <summary>Per-pixel shot noise at full scale, in 8-bit code units (scales with the square root of the signal).</summary>
    public double ShotNoise { get; init; } = 1.2;

    public int RoiPixelCount { get; init; } = 10_000;

    /// <summary>
    /// 0 = white balance truly locked. Above 0, a hidden auto white balance pulls the gains toward a gray-world
    /// estimate by this fraction every frame — the failure the camera trust test must catch.
    /// </summary>
    public double AutoWhiteBalanceDrift { get; init; }

    public int Seed { get; init; } = 12345;
}

/// <summary>A webcam with an unknown response: spectral mixing, white balance, color matrix, tone curve and noise.</summary>
public sealed class SimulatedCamera
{
    private readonly Random _random;
    private Vec3 _hiddenAwbGains = Vec3.One;

    public SimulatedCamera(SimulatedCameraOptions? options = null)
    {
        Options = options ?? new SimulatedCameraOptions();
        if (Options.ExposureLog2Steps.Count == 0)
        {
            throw new ArgumentException("At least one exposure step is required.", nameof(options));
        }

        _random = new Random(Options.Seed);
    }

    public SimulatedCameraOptions Options { get; }

    public IReadOnlyList<double> NominalExposures =>
        [.. Options.ExposureLog2Steps.Order().Select(k => Math.Pow(2, k))];

    /// <summary>The supported nominal exposure closest (in log terms) to the request.</summary>
    public double SnapExposure(double seconds)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(seconds);
        var target = Math.Log2(seconds);
        var best = Options.ExposureLog2Steps.MinBy(k => Math.Abs(k - target));
        return Math.Pow(2, best);
    }

    /// <summary>The exposure the sensor really integrates for a nominal step (unknown to the measurement code).</summary>
    public double ActualExposure(double nominalSeconds)
    {
        var k = (int)Math.Round(Math.Log2(nominalSeconds));
        var sign = (k & 1) == 0 ? 1 : -1;
        return nominalSeconds * (1 + (sign * Options.ExposureRatioError));
    }

    /// <summary>Noise-free encoded output (0–255 per channel) for the given display light and actual exposure.</summary>
    public Vec3 ExpectedCodes(Vec3 displayLight, double actualExposureSeconds)
    {
        var raw = Options.SpectralMix * displayLight * (Options.Sensitivity * actualExposureSeconds);
        var balanced = raw.Scale(Options.WhiteBalanceGains).Scale(_hiddenAwbGains);
        var corrected = Options.ColorCorrection * balanced;
        return corrected.Map(v => Options.Response.Encode(Math.Clamp(v, 0, 1)) * 255.0);
    }

    /// <summary>Captures one frame's region-of-interest statistics.</summary>
    public (ChannelStats Red, ChannelStats Green, ChannelStats Blue) Capture(Vec3 displayLight, double actualExposureSeconds)
    {
        var expected = ExpectedCodes(displayLight, actualExposureSeconds);
        UpdateHiddenAutoWhiteBalance(displayLight);
        return (Stats(expected.X), Stats(expected.Y), Stats(expected.Z));
    }

    private ChannelStats Stats(double expectedCode)
    {
        var sigma = Math.Sqrt((Options.ReadNoise * Options.ReadNoise)
            + (Options.ShotNoise * Options.ShotNoise * Math.Max(expectedCode, 0) / 255.0));
        var mean = expectedCode + (NextGaussian() * sigma / Math.Sqrt(Options.RoiPixelCount));
        var clipped = 1 - NormalCdf((254.5 - expectedCode) / sigma);
        return new ChannelStats(Math.Clamp(mean, 0, 255), sigma, clipped);
    }

    private void UpdateHiddenAutoWhiteBalance(Vec3 displayLight)
    {
        if (Options.AutoWhiteBalanceDrift <= 0)
        {
            return;
        }

        var balanced = (Options.SpectralMix * displayLight).Scale(Options.WhiteBalanceGains).Scale(_hiddenAwbGains);
        if (balanced.Min <= 1e-9)
        {
            return;
        }

        var gray = balanced.Sum / 3;
        var target = _hiddenAwbGains.Scale(new Vec3(gray / balanced.X, gray / balanced.Y, gray / balanced.Z));
        _hiddenAwbGains += (target - _hiddenAwbGains) * Options.AutoWhiteBalanceDrift;
    }

    private double NextGaussian()
    {
        var u1 = 1.0 - _random.NextDouble();
        var u2 = _random.NextDouble();
        return Math.Sqrt(-2 * Math.Log(u1)) * Math.Cos(2 * Math.PI * u2);
    }

    private static double NormalCdf(double z) => 0.5 * (1 + Erf(z / Math.Sqrt(2)));

    private static double Erf(double x)
    {
        // Abramowitz & Stegun 7.1.26, |error| < 1.5e-7: plenty for a clipped-pixel fraction.
        var sign = Math.Sign(x);
        x = Math.Abs(x);
        var t = 1 / (1 + (0.3275911 * x));
        var y = 1 - ((((((1.061405429 * t) - 1.453152027) * t) + 1.421413741) * t - 0.284496736) * t + 0.254829592) * t * Math.Exp(-x * x);
        return sign * y;
    }
}
