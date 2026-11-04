using System.Text.Json.Serialization;

namespace SoftCal.Remote;

/// <summary>The phone link's constants (docs/phone-link.md).</summary>
public static class PhoneLink
{
    /// <summary>Version of the protocol; a side that speaks another refuses the connection. 2 added the focus
    /// command (the PC searches for the focus that blurs the screen); 3 the raw statistics (linear, unprocessed).</summary>
    public const int Protocol = 3;

    /// <summary>The DNS-SD service type the phone advertises.</summary>
    public const string ServiceType = "_softcal._tcp";

    /// <summary>The port the phone listens on (another one when it is taken; the advertisement says which).</summary>
    public const int DefaultPort = 47630;

    /// <summary>The WebSocket's path.</summary>
    public const string Path = "/softcal";
}

/// <summary>
/// One message of the phone link: a JSON object on a WebSocket text frame, its kind in <c>type</c>. The PC sends
/// commands, each with an <c>id</c> its reply repeats; the phone sends the replies, its frames' statistics, and faults.
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(HelloMessage), "hello")]
[JsonDerivedType(typeof(AuthMessage), "auth")]
[JsonDerivedType(typeof(PingMessage), "ping")]
[JsonDerivedType(typeof(PrepareMessage), "prepare")]
[JsonDerivedType(typeof(ExposureMessage), "exposure")]
[JsonDerivedType(typeof(StartStreamMessage), "startStream")]
[JsonDerivedType(typeof(StopStreamMessage), "stopStream")]
[JsonDerivedType(typeof(CaptureImageMessage), "captureImage")]
[JsonDerivedType(typeof(RestoreMessage), "restore")]
[JsonDerivedType(typeof(FocusMessage), "focus")]
[JsonDerivedType(typeof(FocusSetMessage), "focusSet")]
[JsonDerivedType(typeof(ByeMessage), "bye")]
[JsonDerivedType(typeof(ChallengeMessage), "challenge")]
[JsonDerivedType(typeof(WelcomeMessage), "welcome")]
[JsonDerivedType(typeof(PongMessage), "pong")]
[JsonDerivedType(typeof(PreparedMessage), "prepared")]
[JsonDerivedType(typeof(ExposureSetMessage), "exposureSet")]
[JsonDerivedType(typeof(OkMessage), "ok")]
[JsonDerivedType(typeof(FrameMessage), "frame")]
[JsonDerivedType(typeof(ImageMessage), "image")]
[JsonDerivedType(typeof(ErrorMessage), "error")]
[JsonDerivedType(typeof(FaultMessage), "fault")]
public abstract record LinkMessage;

// ---- PC → phone ---------------------------------------------------------------------------------------------------

/// <summary>The PC's first message: its protocol, its name, and its half of the pairing challenge.</summary>
public sealed record HelloMessage(int Protocol, string Client, string Nonce) : LinkMessage;

/// <summary>The PC's proof that it knows the code the phone shows (see <see cref="Pairing"/>).</summary>
public sealed record AuthMessage(string Mac) : LinkMessage;

/// <summary>A clock probe: the phone answers at once with its own time (see <see cref="ClockSync"/>).</summary>
public sealed record PingMessage(long Id, double PcTime) : LinkMessage;

/// <summary>Locks the camera for measuring: exposure, white balance, focus and processing manual or off.</summary>
public sealed record PrepareMessage(long Id, LockSettings Lock) : LinkMessage;

/// <summary>Sets the exposure time (seconds); frames taken with it carry the epoch the reply gives.</summary>
public sealed record ExposureMessage(long Id, double Seconds) : LinkMessage;

/// <summary>Starts sending one <see cref="FrameMessage"/> per camera frame for a region (replacing a stream running).</summary>
public sealed record StartStreamMessage(long Id, RegionSpec Region) : LinkMessage;

public sealed record StopStreamMessage(long Id) : LinkMessage;

/// <summary>Asks for a picture (luma) of a region, averaged over frames, at most <c>MaxWidth</c> pixels wide.</summary>
public sealed record CaptureImageMessage(long Id, RegionSpec Region, int Frames, int Skip, int MaxWidth) : LinkMessage;

/// <summary>Gives the camera back its automatic settings (the end of a measurement).</summary>
public sealed record RestoreMessage(long Id) : LinkMessage;

/// <summary>Moves the locked camera's focus (diopters: 0 the farthest, larger nearer); the exposure stays.</summary>
public sealed record FocusMessage(long Id, double Diopters) : LinkMessage;

public sealed record ByeMessage(string? Reason) : LinkMessage;

// ---- phone → PC ---------------------------------------------------------------------------------------------------

/// <summary>The phone's answer to <see cref="HelloMessage"/>: its half of the challenge, and what it is.</summary>
public sealed record ChallengeMessage(int Protocol, string Nonce, PhoneDescription Phone) : LinkMessage;

/// <summary>The phone accepted the PC's proof and proves in turn that it knows the code; the session is open.</summary>
public sealed record WelcomeMessage(string Mac, PhoneCapabilities Capabilities) : LinkMessage;

public sealed record PongMessage(long Id, double PcTime, double PhoneTime) : LinkMessage;

/// <summary>The lock's report. <c>Raw</c>: while it holds, the statistics come from the sensor's raw data (the phone
/// may find, once its camera is on, that it cannot stream them; its capabilities said what it hoped).</summary>
public sealed record PreparedMessage(long Id, IReadOnlyList<ControlState> Controls, bool Raw = false) : LinkMessage;

/// <summary>The exposure asked for, the one the sensor reports using (null until a frame has it), and its epoch.</summary>
public sealed record ExposureSetMessage(long Id, double Nominal, double? Actual, int Epoch) : LinkMessage;

/// <summary>A command without data to return was carried out.</summary>
public sealed record OkMessage(long Id) : LinkMessage;

/// <summary>The focus asked for, as the camera reports it (which some phones report without moving the lens: the PC
/// judges the picture), whether the lens still moved when the phone stopped waiting, and the phone's time then: frames
/// from that time on show the new focus.</summary>
public sealed record FocusSetMessage(long Id, double Requested, double? Reported, bool Moving, double PhoneTime) : LinkMessage;

/// <summary>
/// One camera frame's region statistics. <c>Time</c> is on the phone's clock (seconds), when the frame's last row
/// finished exposing; <c>Epoch</c> is the exposure request it was taken with, exactly (each capture request carries it);
/// <c>Exposure</c> the time the sensor reports for it. Channel values are the camera's R′G′B′ codes (0–255).
/// </summary>
public sealed record FrameMessage(
    long Seq,
    double Time,
    int Epoch,
    double? Exposure,
    ChannelSummary Red,
    ChannelSummary Green,
    ChannelSummary Blue,
    int Pixels,
    IReadOnlyList<double>? Bands) : LinkMessage;

/// <summary>A picture's header; its pixels (luma, row by row, upright; 8 bits, or 16 little-endian as the mean × 256)
/// follow in the next binary message.</summary>
public sealed record ImageMessage(long Id, int Width, int Height, int Bits, int Frames, double FirstTime, double LastTime) : LinkMessage;

/// <summary>A command failed (<c>Id</c> 0: no command in particular).</summary>
public sealed record ErrorMessage(long Id, string Message) : LinkMessage;

/// <summary>Something went wrong on the phone outside any command (the camera was taken by another app, it overheats…).
/// <c>Kind</c> is an <c>InstrumentFaultKind</c> name.</summary>
public sealed record FaultMessage(string Kind, string Message) : LinkMessage;

// ---- shared parts -------------------------------------------------------------------------------------------------

public sealed record PhoneDescription(string Manufacturer, string Model, string App);

/// <summary>
/// What the phone's camera can do. Exposure in seconds; <c>RowTime</c> the rolling shutter's time per row when known.
/// <c>ManualSensor</c> (exposure and sensitivity set by the app) is required to measure; <c>ManualPostProcessing</c>
/// lets the tone curve be fixed (otherwise the phone's own one stays, which may adapt to the scene). <c>Raw</c>: the
/// statistics come from the sensor's raw data, before any processing, so they are linear and no curve applies.
/// </summary>
public sealed record PhoneCapabilities(
    double MinExposure,
    double MaxExposure,
    int MinIso,
    int MaxIso,
    bool ManualSensor,
    bool ManualPostProcessing,
    bool RealtimeTimestamps,
    int FrameWidth,
    int FrameHeight,
    double FrameRate,
    double? RowTime,
    IReadOnlyList<string> WhiteBalancePresets,
    double? MinimumFocusDiopters,
    bool Raw = false);

/// <summary>How to lock the camera: the white balance as a color temperature (the phone uses the preset nearest to
/// it), and the focus in diopters (null: the farthest the lens goes, which blurs a screen a few centimetres away).</summary>
public sealed record LockSettings(int WhiteBalanceKelvin, double? FocusDiopters);

/// <summary>One control's lock: <c>Control</c> is a <c>CameraControl</c> name, <c>Outcome</c> a <c>LockOutcome</c> name.</summary>
public sealed record ControlState(string Control, string Outcome, double? Requested = null, double? Actual = null, string? Note = null);

/// <summary>A region of the frame in normalized coordinates (0–1), and how many horizontal bands to report.</summary>
public sealed record RegionSpec(double CenterX, double CenterY, double Width, double Height, int RowBands);

/// <summary>One channel over a region: mean, standard deviation, and the fraction of clipped pixels.</summary>
public readonly record struct ChannelSummary(double Mean, double StdDev, double Clipped);

/// <summary>The link's JSON: camelCase, generated at build time (nothing for the phone app's trimmer to cut).</summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals,
    AllowOutOfOrderMetadataProperties = true)]
[JsonSerializable(typeof(LinkMessage))]
internal sealed partial class LinkJson : JsonSerializerContext;
