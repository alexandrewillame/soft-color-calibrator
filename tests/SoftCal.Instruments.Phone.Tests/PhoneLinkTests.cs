using AwesomeAssertions;
using SoftCal.Core.Color;
using SoftCal.Core.Instruments;
using SoftCal.Core.Measurement;
using SoftCal.Instruments.Simulated;
using SoftCal.Remote;
using Xunit;

namespace SoftCal.Instruments.Phone.Tests;

/// <summary>
/// The measurement core reading a phone over a real socket: the simulated rig plays the screen (and the PC's clock) and,
/// behind the link, the phone's camera on a clock of its own.
/// </summary>
public sealed class PhoneLinkTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task The_rig_reads_a_patch_through_the_phone_as_the_screen_shows_it()
    {
        await using var phone = TestPhone.Start();
        await using var client = await phone.ConnectAsync(Token);
        await using var instrument = new PhoneInstrument(client);
        var locked = await instrument.PrepareAsync(new LockRequest(), Token);
        await using var rig = new MeasurementRig(instrument, phone.Rig, new RoiSpec()) { LatencySeconds = 0.1 };
        await rig.SetExposureAsync(1.0 / 64, Token);
        await rig.ReadAsync(new SolidPatch(DeviceRgb.Black), ReadPolicy.Default with { Frames = 4 }, Token);

        var reading = await rig.ReadAsync(new SolidPatch(DeviceRgb.Gray(200)), ReadPolicy.Default with { Frames = 10 }, Token);

        locked.CriticalControlsLocked.Should().BeTrue();
        var camera = phone.Rig.Camera;
        var expected = camera.ExpectedCodes(phone.Rig.Display.Emit(DeviceRgb.Gray(200)), camera.ActualExposure(1.0 / 64));
        reading.Mean.Y.Should().BeApproximately(expected.Y, 0.1, "every averaged frame saw the new patch");
        reading.FirstFrameSeconds.Should().BeGreaterThanOrEqualTo(
            reading.DisplayTimeSeconds + 0.1,
            "frame times come back on the PC's clock, so the settling is judged as for a USB camera");
        reading.FirstFrameSeconds.Should().BeLessThan(reading.DisplayTimeSeconds + 2);
    }

    [Fact]
    public async Task Pings_are_answered_while_the_phone_takes_seconds_to_lock()
    {
        await using var phone = TestPhone.Start();
        var gate = phone.Camera.PrepareGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var client = await phone.ConnectAsync(Token);
        await using var instrument = new PhoneInstrument(client);
        var before = client.Clock.Samples;

        var locking = instrument.PrepareAsync(new LockRequest(), Token);
        for (var i = 0; i < 100 && client.Clock.Samples == before; i++)
        {
            await Task.Delay(100, Token);
        }

        client.Clock.Samples.Should().BeGreaterThan(before, "the ping sent meanwhile came back without waiting for the lock");
        locking.IsCompleted.Should().BeFalse("the phone is still locking");
        client.IsConnected.Should().BeTrue();
        gate.SetResult();
        (await locking).CriticalControlsLocked.Should().BeTrue();
    }

    [Fact]
    public async Task The_focus_moves_on_the_phone_and_its_time_comes_back_on_the_PCs_clock()
    {
        await using var phone = TestPhone.Start();
        await using var client = await phone.ConnectAsync(Token);
        await using var instrument = new PhoneInstrument(client);
        await instrument.PrepareAsync(new LockRequest(), Token);

        var before = phone.Rig.Now;
        var from = await instrument.SetFocusAsync(2, Token);

        phone.Camera.Focus.Should().Be(2);
        from.Should().BeInRange(before - 0.1, phone.Rig.Now + 0.1);
        instrument.FocusCandidates.Should().StartWith(0.05).And.EndWith(10).And.BeInAscendingOrder();
        instrument.DescribeFocus(0.05).Should().Be("the farthest");
        instrument.DescribeFocus(2).Should().Be("2 D (50 cm)");
    }

    [Fact]
    public async Task Exposure_steps_are_the_phones_range_in_doublings()
    {
        await using var phone = TestPhone.Start();
        await using var client = await phone.ConnectAsync(Token);
        await using var instrument = new PhoneInstrument(client);

        var steps = instrument.Capabilities.ExposureSteps;

        steps.Should().NotBeEmpty();
        steps.Zip(steps.Skip(1), (a, b) => b / a).Should().OnlyContain(r => Math.Abs(r - 2) < 1e-12);
        steps[^1].Should().BeLessThanOrEqualTo(PhoneInstrument.LongestExposureSeconds);
        instrument.Capabilities.ReportsActualExposure.Should().BeTrue();
        instrument.Info.Kind.Should().Be(InstrumentKind.RemotePhone);
    }

    [Fact]
    public async Task A_phone_measuring_from_raw_data_gives_linear_readings()
    {
        await using var phone = TestPhone.Start(camera: new SimulatedCameraOptions { Response = CameraResponse.Linear });
        await using var client = await phone.ConnectAsync(Token);
        await using var instrument = new PhoneInstrument(client);
        client.Capabilities.Raw.Should().BeTrue();

        await instrument.PrepareAsync(new LockRequest(), Token);

        instrument.Capabilities.OutputIsLinear.Should().BeTrue();
    }

    [Fact]
    public async Task A_phone_that_cannot_stream_its_raw_data_after_all_is_read_as_processed()
    {
        await using var phone = TestPhone.Start(camera: new SimulatedCameraOptions { Response = CameraResponse.Linear });
        phone.Camera.LosesRawAtLock = true;
        await using var client = await phone.ConnectAsync(Token);
        await using var instrument = new PhoneInstrument(client);
        instrument.Capabilities.OutputIsLinear.Should().BeTrue("the phone hoped to measure from raw data when the link opened");

        await instrument.PrepareAsync(new LockRequest(), Token);

        instrument.Capabilities.OutputIsLinear.Should().BeFalse("its lock said the statistics are processed after all");
        client.Capabilities.Raw.Should().BeFalse();
    }

    [Fact]
    public async Task A_picture_comes_with_its_size_and_times_on_the_PCs_clock()
    {
        await using var phone = TestPhone.Start();
        await using var client = await phone.ConnectAsync(Token);
        await using var instrument = new PhoneInstrument(client);
        await phone.Rig.ShowAsync(new SolidPatch(DeviceRgb.Gray(180)), Token);

        var before = phone.Rig.Now;
        var image = await instrument.CaptureLumaImageAsync(new RoiSpec(0.5, 0.5, 1, 1), 3, 1, Token);

        (image.Width, image.Height).Should().Be((48, 64));
        image.Frames.Should().Be(3);
        image.Pixels.Should().OnlyContain(p => p > 0 && p < 255);
        image.Pixels.Distinct().Should().ContainSingle();
        image.FirstTimestampSeconds.Should().BeInRange(before, phone.Rig.Now);
        image.LastTimestampSeconds.Should().BeGreaterThan(image.FirstTimestampSeconds);
    }

    [Fact]
    public async Task A_phone_without_manual_exposure_is_refused_before_anything_is_locked()
    {
        await using var phone = TestPhone.Start(manualSensor: false);
        await using var client = await phone.ConnectAsync(Token);
        await using var instrument = new PhoneInstrument(client);

        var prepare = () => instrument.PrepareAsync(new LockRequest(), Token);

        instrument.CannotMeasure.Should().Contain("manual");
        await prepare.Should().ThrowAsync<MeasurementException>();
        phone.Camera.Prepares.Should().Be(0);
    }

    [Fact]
    public async Task The_phone_losing_its_camera_stops_the_stream_at_once_with_its_reason()
    {
        await using var phone = TestPhone.Start();
        await using var client = await phone.ConnectAsync(Token);
        await using var instrument = new PhoneInstrument(client);
        var faults = new List<InstrumentFault>();
        instrument.Faulted += (_, fault) =>
        {
            lock (faults)
            {
                faults.Add(fault);
            }
        };
        var frames = 0;
        var streaming = Task.Run(
            async () =>
            {
                await foreach (var _ in instrument.StreamAsync(new RoiSpec(), Token))
                {
                    Interlocked.Increment(ref frames);
                }
            },
            Token);
        await WaitUntil(() => Volatile.Read(ref frames) > 3);

        phone.Camera.RaiseFault(new FaultMessage("Disconnected", "Another app took the camera."));

        var ending = () => streaming;
        await ending.Should().ThrowAsync<MeasurementException>().WithMessage("*Another app took the camera*");
        lock (faults)
        {
            faults.Should().ContainSingle(f => f.Kind == InstrumentFaultKind.Disconnected);
        }
    }

    [Fact]
    public async Task Leaving_a_stream_stops_it_on_the_phone()
    {
        await using var phone = TestPhone.Start();
        await using var client = await phone.ConnectAsync(Token);
        await using var instrument = new PhoneInstrument(client);

        await foreach (var _ in instrument.StreamAsync(new RoiSpec(), Token))
        {
            break;
        }

        await WaitUntil(() => phone.Camera.Streams == 0);
        client.IsConnected.Should().BeTrue();
    }

    [Fact]
    public async Task The_phone_going_away_closes_the_link_and_commands_fail_as_measurement_errors()
    {
        var phone = TestPhone.Start();
        await using var client = await phone.ConnectAsync(Token);
        await using var instrument = new PhoneInstrument(client);
        var gone = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        client.Disconnected += (_, reason) => gone.TrySetResult(reason);

        await phone.DisposeAsync();

        (await gone.Task.WaitAsync(TimeSpan.FromSeconds(10), Token)).Should().NotBeNullOrWhiteSpace();
        client.IsConnected.Should().BeFalse();
        var set = () => instrument.SetExposureAsync(new ExposureRequest(0.01), Token);
        await set.Should().ThrowAsync<MeasurementException>();
    }

    [Fact]
    public async Task The_phone_puts_its_camera_back_in_automatic_when_the_PC_leaves()
    {
        await using var phone = TestPhone.Start();
        var client = await phone.ConnectAsync(Token);
        var instrument = new PhoneInstrument(client, ownsClient: true);
        await instrument.PrepareAsync(new LockRequest(), Token);

        await instrument.DisposeAsync();

        await WaitUntil(() => phone.Camera.Restores > 0);
        phone.Server.Code.Should().NotBeNullOrEmpty();
    }

    private static async Task WaitUntil(Func<bool> condition)
    {
        for (var i = 0; i < 400 && !condition(); i++)
        {
            await Task.Delay(25, Token);
        }

        condition().Should().BeTrue();
    }
}
