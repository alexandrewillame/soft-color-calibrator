using System.Globalization;
using Microsoft.Win32;

namespace SoftCal.Windows.NightLight;

/// <summary>Night light's current state, from a <c>…bluelightreductionstate</c> blob.</summary>
public sealed record NightLightState(bool Active, long? TransitionCode, DateTimeOffset? TransitionUtc);

/// <summary>Night light's settings, from a <c>…bluelightreduction.settings</c> blob.</summary>
public sealed record NightLightSettings(
    bool ScheduleEnabled,
    bool? ScheduleSetHours,
    TimeOnly? ScheduleOn,
    TimeOnly? ScheduleOff,
    int? ColorTemperatureKelvin,
    TimeOnly? SunsetTime,
    TimeOnly? SunriseTime);

/// <summary>One CloudStore key and what it decodes to.</summary>
public sealed record NightLightBlob(
    string KeyName,
    string Scope,
    string Kind,
    DateTimeOffset? ModifiedUtc,
    string RawHex,
    NightLightState? State,
    NightLightSettings? Settings,
    string? Error);

/// <summary>Everything the Night light probe found, with the effective reading.</summary>
public sealed record NightLightReport(IReadOnlyList<NightLightBlob> Blobs, bool? Active, int? ColorTemperatureKelvin, bool? ScheduleEnabled, string Summary);

/// <summary>
/// Reads Night light's state from the CloudStore registry blobs — the global keys and the per-device keys newer builds
/// add — and decodes their Bond payloads. Strictly read-only: the format is undocumented, so the app never writes it;
/// the user turns Night light off in Settings.
/// </summary>
public static class NightLightReader
{
    public const string StoreKey = @"Software\Microsoft\Windows\CurrentVersion\CloudStore\Store\DefaultAccount\Current";

    public static NightLightReport Read()
    {
        var blobs = new List<NightLightBlob>();
        using var store = Registry.CurrentUser.OpenSubKey(StoreKey, writable: false);
        if (store is not null)
        {
            foreach (var name in store.GetSubKeyNames().Where(n => n.Contains("windows.data.bluelightreduction", StringComparison.OrdinalIgnoreCase)))
            {
                using var outer = store.OpenSubKey(name, writable: false);
                var innerName = name[(name.IndexOf('$', StringComparison.Ordinal) + 1)..];
                using var inner = outer?.OpenSubKey(innerName, writable: false);
                if (inner?.GetValue("Data") is byte[] data)
                {
                    blobs.Add(Decode(name, data));
                }
            }
        }

        return Summarize(blobs);
    }

    /// <summary>Decodes one CloudStore value (the outer envelope and the Bond payload inside it).</summary>
    public static NightLightBlob Decode(string keyName, byte[] data)
    {
        ArgumentNullException.ThrowIfNull(keyName);
        ArgumentNullException.ThrowIfNull(data);
        var dollar = keyName.IndexOf('$', StringComparison.Ordinal);
        var prefix = dollar > 0 ? keyName[..dollar] : keyName;
        var scope = prefix.Equals("default", StringComparison.OrdinalIgnoreCase) ? "global" : "per-device " + prefix;
        var kind = keyName.Contains("settings", StringComparison.OrdinalIgnoreCase) ? "settings" : "state";
        var hex = Convert.ToHexString(data);
        try
        {
            // Envelope: { 0: { 0: bool }, 1: { 0: unix seconds, 1: { 1: list<int8> payload } } }
            var envelope = BondCompactReader.ParseMarshaled(data);
            var body = envelope.Struct(1) ?? throw new FormatException("Missing envelope body.");
            DateTimeOffset? modified = body.UInt(0) is { } unix && unix < 32503680000UL ? DateTimeOffset.FromUnixTimeSeconds((long)unix) : null;
            var payloadBytes = body.Struct(1)?.Bytes(1) ?? throw new FormatException("Missing payload.");
            var payload = BondCompactReader.ParseMarshaled(payloadBytes);
            return kind == "state"
                ? new NightLightBlob(keyName, scope, kind, modified, hex, DecodeState(payload), null, null)
                : new NightLightBlob(keyName, scope, kind, modified, hex, null, DecodeSettings(payload), null);
        }
        catch (FormatException ex)
        {
            return new NightLightBlob(keyName, scope, kind, null, hex, null, null, ex.Message);
        }
    }

    /// <summary>State payload: field 0 is present only while Night light is on; field 20 is the FILETIME of the last switch.</summary>
    public static NightLightState DecodeState(BondStruct payload)
    {
        ArgumentNullException.ThrowIfNull(payload);
        DateTimeOffset? transition = null;
        if (payload.UInt(20) is { } fileTime && fileTime is > 0 and < 2650467743999999999UL)
        {
            transition = DateTimeOffset.FromFileTime((long)fileTime).ToUniversalTime();
        }

        return new NightLightState(payload[0] is not null, payload.Int(10), transition);
    }

    /// <summary>Settings payload: 0 = schedule on, 10 = schedule type, 20/30 = custom on/off times, 40 = colour
    /// temperature (K), 50/60 = sunset/sunrise times. Field meanings come from community reverse engineering.</summary>
    public static NightLightSettings DecodeSettings(BondStruct payload)
    {
        ArgumentNullException.ThrowIfNull(payload);
        static TimeOnly? Time(BondStruct? s) => s is null
            ? null
            : new TimeOnly((int)Math.Clamp(s.Int(0) ?? 0, 0, 23), (int)Math.Clamp(s.Int(1) ?? 0, 0, 59));
        var kelvin = payload.Int(40);
        return new NightLightSettings(
            payload.Int(0) == 1,
            payload[10] is null ? null : payload.Int(10) == 1,
            Time(payload.Struct(20)),
            Time(payload.Struct(30)),
            kelvin is null ? null : (int)kelvin,
            Time(payload.Struct(50)),
            Time(payload.Struct(60)));
    }

    /// <summary>
    /// The most recently modified blob of each kind wins (ties go to the per-device key). On build 26300, switching
    /// Night light in Settings rewrites the global key and can leave an older per-device key behind.
    /// </summary>
    public static NightLightReport Summarize(IReadOnlyList<NightLightBlob> blobs)
    {
        ArgumentNullException.ThrowIfNull(blobs);
        NightLightBlob? Pick(string kind) => blobs
            .Where(b => b.Kind == kind && b.Error is null)
            .OrderByDescending(b => b.ModifiedUtc ?? DateTimeOffset.MinValue)
            .ThenByDescending(b => b.Scope.StartsWith("per-device", StringComparison.Ordinal))
            .FirstOrDefault();
        var state = Pick("state");
        var settings = Pick("settings");
        var active = state?.State?.Active;
        var kelvin = settings?.Settings?.ColorTemperatureKelvin;
        var schedule = settings?.Settings?.ScheduleEnabled;
        var disagree = blobs.Where(b => b.State is not null).Select(b => b.State!.Active).Distinct().Count() > 1;
        var summary = active switch
        {
            null => "Night light state unknown (no readable CloudStore blob): ask the user.",
            true => string.Create(CultureInfo.InvariantCulture, $"Night light is ON ({kelvin?.ToString(CultureInfo.InvariantCulture) ?? "?"} K, schedule {(schedule == true ? "on" : "off")}); turn it off in Settings before measuring."),
            false => schedule == true
                ? "Night light is off now but scheduled: it may switch on during the session."
                : "Night light is off.",
        };
        if (disagree)
        {
            summary += $" (The global and per-device keys disagree; the newest, {state?.Scope}, was used.)";
        }

        return new NightLightReport(blobs, active, kelvin, schedule, summary);
    }
}
