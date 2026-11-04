using System.Runtime.CompilerServices;
using AwesomeAssertions;
using SoftCal.Core.Instruments;
using SoftCal.Core.Measurement;
using Xunit;

namespace SoftCal.Instruments.Simulated.Tests;

/// <summary>
/// Regression for the first real measurement on the C920e: a saturated red line pattern clipped ~17 % of its pixels
/// while its mean was on the auto-exposure target, so every fine reading of the red null was rejected.
/// </summary>
public sealed class AutoExposeClippingTests
{
    [Fact]
    public async Task Auto_exposure_backs_off_when_a_bright_patch_clips_some_pixels_below_its_mean_target()
    {
        var token = TestContext.Current.CancellationToken;
        var sim = new SimulatedRig(new SimulatedDisplay(), new SimulatedCamera());
        var instrument = new PixelClippingInstrument(sim);
        await using var rig = new MeasurementRig(instrument, sim, new RoiSpec()) { LatencySeconds = sim.Options.LatencySeconds };
        var red = new HalftonePatch(DeviceRgb.Primary(0, 255), DeviceRgb.Black, HalftoneGeometry.HorizontalLines);

        await rig.AutoExposeAsync(red, 140, token);
        var reading = await rig.ReadAsync(red, ReadPolicy.Default with { Frames = 6 }, token);

        reading.MaxClippedFraction.Should().BeLessThan(0.0005);
        reading.Mean.X.Should().BeLessThanOrEqualTo(PixelClippingInstrument.ClipsAbove);
    }

    [Fact]
    public async Task Dark_channels_at_the_black_clamp_do_not_count_as_clipping()
    {
        var token = TestContext.Current.CancellationToken;
        var sim = new SimulatedRig(new SimulatedDisplay(), new SimulatedCamera());
        var instrument = new PixelClippingInstrument(sim);
        await using var rig = new MeasurementRig(instrument, sim, new RoiSpec()) { LatencySeconds = sim.Options.LatencySeconds };
        var green = new HalftonePatch(DeviceRgb.Primary(1, 255), DeviceRgb.Black, HalftoneGeometry.HorizontalLines);

        await rig.AutoExposeAsync(green, 140, token);
        var reading = await rig.ReadAsync(green, ReadPolicy.Default with { Frames = 6 }, token);

        reading.Mean.Y.Should().BeGreaterThan(60, "the exposure must not be cut down because of the empty channels");
        reading.MaxClippedFraction.Should().BeLessThan(0.0005);
    }

    /// <summary>
    /// Like the C920e: 17 % clipped pixels in a channel whose mean is above <see cref="ClipsAbove"/>, and 58 % "clipped"
    /// (black-clamped) pixels in channels sitting near black.
    /// </summary>
    private sealed class PixelClippingInstrument(IMeasurementInstrument inner) : IMeasurementInstrument
    {
        public const double ClipsAbove = 120;

        public event EventHandler<InstrumentFault>? Faulted
        {
            add => inner.Faulted += value;
            remove => inner.Faulted -= value;
        }

        public InstrumentInfo Info => inner.Info;

        public InstrumentCapabilities Capabilities => inner.Capabilities;

        public Task<LockReport> PrepareAsync(LockRequest request, CancellationToken cancellationToken) => inner.PrepareAsync(request, cancellationToken);

        public Task<ExposureState> SetExposureAsync(ExposureRequest request, CancellationToken cancellationToken) => inner.SetExposureAsync(request, cancellationToken);

        public async IAsyncEnumerable<FrameStats> StreamAsync(RoiSpec roi, [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            await foreach (var f in inner.StreamAsync(roi, cancellationToken))
            {
                static ChannelStats Clip(ChannelStats c) => c with
                {
                    ClippedFraction = c.Mean > ClipsAbove ? 0.17 : c.Mean < 12 ? 0.58 : c.ClippedFraction,
                };

                yield return f with { Red = Clip(f.Red), Green = Clip(f.Green), Blue = Clip(f.Blue) };
            }
        }

        public Task RestoreAsync(CancellationToken cancellationToken) => inner.RestoreAsync(cancellationToken);

        public ValueTask DisposeAsync() => inner.DisposeAsync();
    }
}
