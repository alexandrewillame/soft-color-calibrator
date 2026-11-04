using System.Globalization;
using System.Text.Json;
using AwesomeAssertions;
using SoftCal.Core.Instruments;
using SoftCal.Core.Measurement;
using SoftCal.Core.Sessions;
using Xunit;

namespace SoftCal.Core.Tests.Sessions;

public sealed class SessionTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "softcal-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    private static SessionHeader Header(int schema = SessionHeader.CurrentSchemaVersion) => new()
    {
        SchemaVersion = schema,
        SessionId = Guid.Parse("8f5e2a10-1111-4c3b-9a77-0123456789ab"),
        CreatedUtc = new DateTimeOffset(2026, 10, 15, 6, 45, 0, TimeSpan.Zero),
        AppVersion = "0.1.0",
        DisplayKey = "BNQ7F2B-0123456789",
        DisplayName = "ZOWIE XL LCD",
        Instrument = new InstrumentInfo("cam-1", InstrumentKind.UsbWebcam, "Logi Webcam C920e"),
        Purpose = "unit test",
    };

    [Fact]
    public void Header_and_events_round_trip()
    {
        using var state = JsonDocument.Parse("""{"nightLight":false,"kelvin":2631.5}""");
        var frame = new FrameStats(
            7,
            1.25,
            3,
            new ChannelStats(201.5, 1.1, 0),
            new ChannelStats(198.25, 1.0, 0.001),
            new ChannelStats(190.125, 1.2, 0),
            10_000,
            [0.5, 0.25]);
        SessionEvent[] written =
        [
            new PatchShownEvent(0.5, 1, new SolidPatch(DeviceRgb.Gray(128)) { Layout = new PatchLayout(0.4, 0.6, 0.3, DeviceRgb.Gray(20)) }),
            new PatchShownEvent(0.75, 2, new HalftonePatch(DeviceRgb.White, DeviceRgb.Black, HalftoneGeometry.Bayer4x4, 5)),
            new FrameEvent(1.25, frame),
            new ExposureChangedEvent(1.5, new ExposureState(1.0 / 32, null, 4)),
            new StateSnapshotEvent(2.0, "windows.nightLight", state.RootElement.Clone()),
            new NoteEvent(2.5, "camera remounted"),
        ];

        using (var recorder = SessionRecorder.Create(_directory, Header()))
        {
            foreach (var e in written)
            {
                recorder.Append(e);
            }
        }

        var read = SessionReader.Read(_directory);

        read.Header.Should().Be(Header());
        read.LastLineWasTruncated.Should().BeFalse();
        read.Events.Should().HaveCount(written.Length);
        read.Events[0].Should().Be(written[0]);
        read.Events[1].Should().BeOfType<PatchShownEvent>()
            .Which.Patch.Should().BeOfType<HalftonePatch>()
            .Which.OnFraction.Should().Be(5 / 16.0);
        var readFrame = read.Events[2].Should().BeOfType<FrameEvent>().Which.Frame;
        readFrame.Green.Should().Be(frame.Green);
        readFrame.RowBandMeans.Should().Equal(0.5, 0.25);
        read.Events[3].Should().Be(written[3]);
        read.Events[4].Should().BeOfType<StateSnapshotEvent>()
            .Which.State.GetProperty("kelvin").GetDouble().Should().Be(2631.5);
        read.Events[5].Should().Be(written[5]);
    }

    [Fact]
    public void A_truncated_last_line_is_tolerated()
    {
        using (var recorder = SessionRecorder.Create(_directory, Header()))
        {
            recorder.Append(new NoteEvent(1, "first"));
            recorder.Append(new NoteEvent(2, "second"));
        }

        File.AppendAllText(Path.Combine(_directory, SessionRecorder.EventsFileName), "{\"type\":\"note\",\"timeSe");

        var read = SessionReader.Read(_directory);

        read.Events.Should().HaveCount(2);
        read.LastLineWasTruncated.Should().BeTrue();
    }

    [Fact]
    public void Files_are_culture_invariant()
    {
        var previous = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("fr-FR");
        try
        {
            using (var recorder = SessionRecorder.Create(_directory, Header()))
            {
                recorder.Append(new ExposureChangedEvent(0.5, new ExposureState(0.03125, null, 1)));
            }
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }

        var text = File.ReadAllText(Path.Combine(_directory, SessionRecorder.EventsFileName));

        text.Should().Contain("0.03125").And.NotContain("0,03125");
    }

    [Fact]
    public void A_newer_schema_is_refused()
    {
        using (SessionRecorder.Create(_directory, Header(schema: SessionHeader.CurrentSchemaVersion + 1)))
        {
        }

        FluentActions.Invoking(() => SessionReader.Read(_directory)).Should().Throw<InvalidDataException>();
    }

    [Fact]
    public void An_existing_session_is_never_overwritten()
    {
        using (SessionRecorder.Create(_directory, Header()))
        {
        }

        FluentActions.Invoking(() => SessionRecorder.Create(_directory, Header())).Should().Throw<IOException>();
    }
}
