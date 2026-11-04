using System.Text.Json;
using System.Text.Json.Serialization;
using SoftCal.Core.Instruments;
using SoftCal.Core.Measurement;

namespace SoftCal.Core.Sessions;

/// <summary>
/// The session document header (session.json). Together with the append-only event log (events.jsonl) it holds every
/// raw reading and state snapshot, so analysis is a pure function of the files and old sessions can be re-analyzed.
/// </summary>
public sealed record SessionHeader
{
    public const int CurrentSchemaVersion = 1;

    public int SchemaVersion { get; init; } = CurrentSchemaVersion;

    public required Guid SessionId { get; init; }

    public required DateTimeOffset CreatedUtc { get; init; }

    public required string AppVersion { get; init; }

    public string? DisplayKey { get; init; }

    public string? DisplayName { get; init; }

    public InstrumentInfo? Instrument { get; init; }

    public InstrumentCapabilities? InstrumentCapabilities { get; init; }

    public string? Purpose { get; init; }
}

/// <summary>One line of the event log. <see cref="TimeSeconds"/> is on the host's monotonic session clock.</summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(PatchShownEvent), "patch")]
[JsonDerivedType(typeof(FrameEvent), "frame")]
[JsonDerivedType(typeof(ExposureChangedEvent), "exposure")]
[JsonDerivedType(typeof(StateSnapshotEvent), "state")]
[JsonDerivedType(typeof(NoteEvent), "note")]
public abstract record SessionEvent(double TimeSeconds);

public sealed record PatchShownEvent(double TimeSeconds, long Sequence, PatchSpec Patch) : SessionEvent(TimeSeconds);

public sealed record FrameEvent(double TimeSeconds, FrameStats Frame) : SessionEvent(TimeSeconds);

public sealed record ExposureChangedEvent(double TimeSeconds, ExposureState State) : SessionEvent(TimeSeconds);

/// <summary>A snapshot of some system state (OS color settings, OSD values, display mode, camera controls…).</summary>
public sealed record StateSnapshotEvent(double TimeSeconds, string Kind, JsonElement State) : SessionEvent(TimeSeconds);

public sealed record NoteEvent(double TimeSeconds, string Text) : SessionEvent(TimeSeconds);

/// <summary>A session read back from disk.</summary>
public sealed record SessionRecording(SessionHeader Header, IReadOnlyList<SessionEvent> Events, bool LastLineWasTruncated);

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    UseStringEnumConverter = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    AllowOutOfOrderMetadataProperties = true)]
[JsonSerializable(typeof(SessionHeader))]
[JsonSerializable(typeof(SessionEvent))]
internal sealed partial class SessionJsonContext : JsonSerializerContext;
