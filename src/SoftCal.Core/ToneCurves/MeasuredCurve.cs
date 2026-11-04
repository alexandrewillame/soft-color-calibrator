namespace SoftCal.Core.ToneCurves;

/// <summary>
/// A transfer curve built from measured (encoded, linear) samples, interpolated with <see cref="MonotoneCubic"/>.
/// Samples are normalized so that the first point maps to 0 and the last to 1.
/// </summary>
public sealed class MeasuredCurve : ITransferCurve
{
    private readonly MonotoneCubic _interpolator;

    public MeasuredCurve(IReadOnlyList<double> encoded, IReadOnlyList<double> linear)
    {
        ArgumentNullException.ThrowIfNull(encoded);
        ArgumentNullException.ThrowIfNull(linear);
        if (encoded.Count != linear.Count || encoded.Count < 2)
        {
            throw new ArgumentException("Need at least two (encoded, linear) samples of equal count.", nameof(linear));
        }

        var low = linear[0];
        var high = linear[^1];
        if (!(high > low))
        {
            throw new ArgumentException("The last sample must be brighter than the first.", nameof(linear));
        }

        var normalized = linear.Select(v => (v - low) / (high - low)).ToArray();
        _interpolator = new MonotoneCubic(encoded, normalized);
        IsMonotone = _interpolator.IsNonDecreasing;
    }

    /// <summary>False when the measurements contain a reversal (a sign of noise, clipping or a broken display mode).</summary>
    public bool IsMonotone { get; }

    public double ToLinear(double encoded) => Math.Clamp(_interpolator.Evaluate(encoded), 0, 1);

    public double ToEncoded(double linear) => _interpolator.Inverse(Math.Clamp(linear, 0, 1));
}
