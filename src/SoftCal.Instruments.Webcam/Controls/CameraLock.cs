using System.Runtime.InteropServices;
using SoftCal.Core.Instruments;

namespace SoftCal.Instruments.Webcam.Controls;

/// <summary>One control as captured before the lock: its range (null = unsupported) and value (null = unreadable).</summary>
public sealed record ControlSnapshotEntry(
    UvcControlSet Set,
    int Property,
    string Name,
    UvcControlRange? Range,
    UvcControlValue? Value)
{
    public UvcControlId Id => new(Set, Property);
}

/// <summary>Every control's state before the lock. It is journaled so a crashed run can still be restored.</summary>
public sealed record ControlSnapshot(string DeviceId, DateTimeOffset TakenUtc, IReadOnlyList<ControlSnapshotEntry> Entries)
{
    public static ControlSnapshot Capture(IUvcControlBackend backend, string deviceId, TimeProvider? clock = null)
    {
        ArgumentNullException.ThrowIfNull(backend);
        var entries = new List<ControlSnapshotEntry>();
        foreach (var id in UvcControlCatalog.All.Concat(UvcControlCatalog.ReadOnly))
        {
            var range = Try(() => backend.GetRange(id));
            var value = Try(() => backend.Get(id));
            entries.Add(new ControlSnapshotEntry(id.Set, id.Property, UvcControlCatalog.NameOf(id), range, value));
        }

        return new ControlSnapshot(deviceId, (clock ?? TimeProvider.System).GetUtcNow(), entries);
    }

    public ControlSnapshotEntry? Find(UvcControlId id) => Entries.FirstOrDefault(e => e.Id == id);

    private static T? Try<T>(Func<T?> read)
    {
        try
        {
            return read();
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or InvalidOperationException or NotSupportedException)
        {
            return default;
        }
    }
}

/// <summary>A write the lock performs, and why.</summary>
public sealed record PlannedWrite(UvcControlId Id, UvcControlValue Value, string Reason);

/// <summary>Camera-specific lock settings that the platform-neutral <see cref="LockRequest"/> does not carry.</summary>
public sealed record WebcamLockSettings
{
    /// <summary>Initial manual exposure, in Windows' log2-seconds units (−6 = 1/64 s).</summary>
    public int InitialExposureLog2 { get; init; } = -6;
}

/// <summary>Read-back of one control after the lock.</summary>
public sealed record ControlVerification(
    UvcControlId Id,
    string Name,
    UvcControlValue? Before,
    UvcControlValue? Requested,
    UvcControlValue? After,
    LockOutcome Outcome,
    string? Note);

/// <summary>Plans and verifies the camera lock; the order of operations is the caller's (write before streaming).</summary>
public static class CameraLock
{
    /// <summary>
    /// The writes that lock the camera: white balance, exposure, gain, backlight compensation, sharpness and focus set
    /// to fixed manual values, and every other writable control explicitly set to its default (manual mode), so that no
    /// automatic processing and no per-camera defaults from Windows Settings stay in effect.
    /// </summary>
    public static IReadOnlyList<PlannedWrite> Plan(ControlSnapshot snapshot, LockRequest request, WebcamLockSettings settings)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(settings);
        var writes = new List<PlannedWrite>();
        foreach (var entry in snapshot.Entries)
        {
            var id = entry.Id;
            if (UvcControlCatalog.ReadOnly.Contains(id))
            {
                continue;
            }

            (int Value, string Reason)? target = null;
            if (id == UvcControlCatalog.PowerLineFrequency || id == UvcControlCatalog.AutoExposurePriority)
            {
                // Reached through IKsControl, which does not report a default: pin the current value.
                if (entry.Value is { } current)
                {
                    writes.Add(new PlannedWrite(id, new UvcControlValue(current.Value, IsAuto: false), "kept at its current value"));
                }

                continue;
            }

            if (entry.Range is not { } range)
            {
                continue;
            }

            if (id == UvcControlCatalog.WhiteBalance)
            {
                target = (range.Clamp(request.WhiteBalanceKelvin), "fixed white balance");
            }
            else if (id == UvcControlCatalog.Exposure)
            {
                target = (range.Clamp(settings.InitialExposureLog2), "manual exposure");
            }
            else if (id == UvcControlCatalog.Gain)
            {
                target = (request.MinimizeGain ? range.Min : range.Default, request.MinimizeGain ? "minimum gain" : "default gain");
            }
            else if (id == UvcControlCatalog.BacklightCompensation)
            {
                target = (range.Clamp(0), "backlight compensation off");
            }
            else if (id == UvcControlCatalog.Sharpness)
            {
                target = (request.MinimizeSharpness ? range.Min : range.Default, request.MinimizeSharpness ? "minimum sharpness" : "default sharpness");
            }
            else if (id == UvcControlCatalog.Focus)
            {
                target = (range.Clamp(request.FocusValue ?? range.Min), request.FocusValue is null ? "manual focus, far end (defocused)" : "manual focus");
            }
            else
            {
                target = (range.Default, "default, written explicitly");
            }

            if (target is { } t)
            {
                writes.Add(new PlannedWrite(id, new UvcControlValue(t.Value, IsAuto: false), t.Reason));
            }
        }

        return writes;
    }

    /// <summary>Compares read-back values with the planned writes.</summary>
    public static IReadOnlyList<ControlVerification> Verify(
        ControlSnapshot before,
        IReadOnlyList<PlannedWrite> writes,
        IReadOnlyDictionary<UvcControlId, string> writeErrors,
        Func<UvcControlId, UvcControlValue?> readBack)
    {
        ArgumentNullException.ThrowIfNull(before);
        ArgumentNullException.ThrowIfNull(writes);
        ArgumentNullException.ThrowIfNull(writeErrors);
        ArgumentNullException.ThrowIfNull(readBack);
        var results = new List<ControlVerification>();
        foreach (var entry in before.Entries)
        {
            var write = writes.FirstOrDefault(w => w.Id == entry.Id);
            var after = readBack(entry.Id);
            LockOutcome outcome;
            string? note = write?.Reason;
            if (entry.Range is null && entry.Value is null)
            {
                outcome = LockOutcome.NotSupported;
            }
            else if (write is null)
            {
                outcome = UvcControlCatalog.ReadOnly.Contains(entry.Id) ? LockOutcome.Locked : LockOutcome.NotSupported;
                note = UvcControlCatalog.ReadOnly.Contains(entry.Id) ? "read only" : "not writable";
            }
            else if (writeErrors.TryGetValue(entry.Id, out var error))
            {
                outcome = LockOutcome.Failed;
                note = error;
            }
            else if (after is not { } actual)
            {
                outcome = LockOutcome.Failed;
                note = "could not read back";
            }
            else if (actual.Value != write.Value.Value || actual.IsAuto)
            {
                outcome = LockOutcome.VerifiedMismatch;
            }
            else
            {
                outcome = LockOutcome.Locked;
            }

            results.Add(new ControlVerification(entry.Id, entry.Name, entry.Value, write?.Value, after, outcome, note));
        }

        return results;
    }

    /// <summary>Summarizes the verification in platform-neutral terms.</summary>
    public static LockReport ToLockReport(IReadOnlyList<ControlVerification> verifications, IEnumerable<ControlLockResult>? extra = null)
    {
        ArgumentNullException.ThrowIfNull(verifications);
        var results = new List<ControlLockResult>();
        foreach (var v in verifications)
        {
            if (UvcControlCatalog.ToCameraControl(v.Id) is { } control)
            {
                results.Add(new ControlLockResult(control, v.Outcome, v.Requested?.Value, v.After?.Value, v.Note));
            }
        }

        if (extra is not null)
        {
            results.AddRange(extra);
        }

        return new LockReport(results);
    }

    /// <summary>
    /// The writes that put every snapshotted control back, auto modes included. While exposure is automatic the gain the
    /// camera reports is its own momentary choice, not a user setting, so it goes back to the camera's default instead
    /// of being pinned at that value.
    /// </summary>
    public static IReadOnlyList<PlannedWrite> PlanRestore(ControlSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        return PlanRestoreCore(snapshot);
    }

    /// <summary>
    /// Writes a snapshot back in two passes: first every control at its value in manual mode (an automatic one at the
    /// value it had, as its starting point), then, <paramref name="between"/> later, the automatic ones switched back
    /// to automatic; each control is read back. The camera should stream throughout: a C920e switched back to
    /// automatic while idle reads "automatic" but keeps the locked white balance and exposure (its picture stayed at
    /// 6500 K, its gain at the maximum, in every app after). Returns the controls that failed.
    /// </summary>
    public static async Task<IReadOnlyList<string>> ApplyRestoreAsync(IUvcControlBackend backend, ControlSnapshot snapshot, TimeSpan between, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(backend);
        ArgumentNullException.ThrowIfNull(snapshot);
        var writes = PlanRestoreCore(snapshot);
        var failed = new List<string>();
        void Write(PlannedWrite write, UvcControlValue value)
        {
            try
            {
                backend.Set(write.Id, value);
            }
            catch (Exception ex) when (ex is COMException or NotSupportedException or ArgumentException or InvalidOperationException)
            {
                failed.Add(UvcControlCatalog.NameOf(write.Id));
            }
        }

        foreach (var write in writes)
        {
            Write(write, write.Value with { IsAuto = false });
        }

        var automatic = writes.Where(w => w.Value.IsAuto).ToList();
        if (automatic.Count > 0 && between > TimeSpan.Zero)
        {
            await Task.Delay(between, cancellationToken).ConfigureAwait(false);
        }

        foreach (var write in automatic)
        {
            Write(write, write.Value);
        }

        foreach (var write in writes)
        {
            var back = backend.Get(write.Id);
            if (back is not { } b || b.IsAuto != write.Value.IsAuto || (!write.Value.IsAuto && b.Value != write.Value.Value))
            {
                failed.Add(UvcControlCatalog.NameOf(write.Id));
            }
        }

        return [.. failed.Distinct()];
    }

    private static List<PlannedWrite> PlanRestoreCore(ControlSnapshot snapshot)
    {
        var autoExposure = snapshot.Find(UvcControlCatalog.Exposure)?.Value?.IsAuto == true;
        return
        [
            .. snapshot.Entries
                .Where(e => e.Value is not null && !UvcControlCatalog.ReadOnly.Contains(e.Id))
                .Select(e => autoExposure && e.Id == UvcControlCatalog.Gain && e.Range is { } r
                    ? new PlannedWrite(e.Id, new UvcControlValue(r.Default, IsAuto: false), "restore (default: gain follows auto exposure)")
                    : new PlannedWrite(e.Id, e.Value!.Value, "restore")),
        ];
    }
}
