using System.ComponentModel;
using System.Diagnostics;

namespace SoftCal.Windows.Safety;

/// <summary>
/// A process identified by its id <em>and</em> start time. Windows reuses process ids, so a bare id left in the journal
/// by a crashed run could later belong to an unrelated process and make the dead run look alive forever.
/// </summary>
public sealed record ProcessIdentity(int Id, DateTimeOffset? StartTimeUtc)
{
    private static readonly Lazy<ProcessIdentity> CurrentIdentity = new(() => Of(Environment.ProcessId) ?? new(Environment.ProcessId, null));

    public static ProcessIdentity Current => CurrentIdentity.Value;

    /// <summary>The identity of a running process, or null when no process has that id.</summary>
    public static ProcessIdentity? Of(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            return new ProcessIdentity(processId, StartOf(process));
        }
        catch (ArgumentException)
        {
            return null;
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }

    /// <summary>
    /// True when a process with this id is running and, if the start time is known, started at that time. A process we
    /// cannot inspect is not ours (journal owners run as the current user), so it only counts when no start time is known.
    /// </summary>
    public bool IsAlive()
    {
        if (Id == Environment.ProcessId && (StartTimeUtc is null || SameStart(Current.StartTimeUtc, StartTimeUtc)))
        {
            return true;
        }

        try
        {
            using var process = Process.GetProcessById(Id);
            if (process.HasExited)
            {
                return false;
            }

            return StartTimeUtc is null || SameStart(StartOf(process), StartTimeUtc);
        }
        catch (ArgumentException)
        {
            return false;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
        catch (Win32Exception)
        {
            return StartTimeUtc is null;
        }
    }

    private static DateTimeOffset? StartOf(Process process)
    {
        try
        {
            return new DateTimeOffset(process.StartTime.ToUniversalTime(), TimeSpan.Zero);
        }
        catch (Win32Exception)
        {
            return null;
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }

    private static bool SameStart(DateTimeOffset? a, DateTimeOffset? b) =>
        a is { } x && b is { } y && Math.Abs((x - y).TotalSeconds) < 1;
}
