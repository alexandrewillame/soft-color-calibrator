using System.ComponentModel;
using System.Diagnostics;
using System.Reflection;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SoftCal.Windows.Elevation;

/// <summary>
/// Everything the elevated helper will ever do. It is an allowlist: the helper refuses anything else, so a tampered
/// request can never make it run arbitrary commands. Later milestones add tasks (calibration loader, profile install,
/// firewall rule for the phone link).
/// </summary>
public enum ElevatedTaskKind
{
    /// <summary>Reports whether the helper really runs elevated; used to test the UAC round trip.</summary>
    Ping,

    /// <summary>Turns on Windows' calibration loader: 'Use Windows display calibration' and its scheduled task.</summary>
    TurnOnCalibrationLoader,

    /// <summary>Puts the calibration loader back as it was (arguments <c>switchOn</c>, <c>taskEnabled</c>: true/false).</summary>
    RestoreCalibrationLoader,
}

public sealed record ElevatedRequest(Guid Id, ElevatedTaskKind Task, IReadOnlyDictionary<string, string> Arguments);

public sealed record ElevatedResponse(Guid Id, bool Success, string Message, bool RanElevated);

/// <summary>
/// The narrow elevated helper. The caller writes a request file and starts this same executable elevated (one UAC prompt)
/// with <c>--elevated &lt;request&gt;</c>; the elevated copy validates the request against the allowlist, runs it, writes a
/// response next to it, and exits. Executables route that argument to <see cref="TryRunFromCommandLine"/> first thing.
/// </summary>
public static class ElevatedTasks
{
    public const string Verb = "--elevated";

    private const string RequestSuffix = ".request.json";
    private const string ResponseSuffix = ".response.json";

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() },
    };

    /// <summary>%LOCALAPPDATA%\SoftColorCalibrator\elevated</summary>
    public static string RequestDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SoftColorCalibrator", "elevated");

    /// <summary>Runs one allowlisted task elevated. Shows a UAC prompt; a declined prompt is a failed response, not an exception.</summary>
    public static async Task<ElevatedResponse> RunAsync(
        ElevatedTaskKind task,
        IReadOnlyDictionary<string, string>? arguments,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(RequestDirectory);
        var request = new ElevatedRequest(Guid.NewGuid(), task, arguments ?? new Dictionary<string, string>());
        var requestPath = Path.Combine(RequestDirectory, request.Id.ToString("N") + RequestSuffix);
        var responsePath = ResponsePathFor(requestPath);
        await File.WriteAllTextAsync(requestPath, JsonSerializer.Serialize(request, Json), new UTF8Encoding(false), cancellationToken).ConfigureAwait(false);
        try
        {
            var start = new ProcessStartInfo(Environment.ProcessPath ?? throw new InvalidOperationException("Cannot locate the running executable."))
            {
                UseShellExecute = true,
                Verb = "runas",
                WindowStyle = ProcessWindowStyle.Hidden,
            };
            if (Path.GetFileNameWithoutExtension(start.FileName).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
            {
                start.ArgumentList.Add(Assembly.GetEntryAssembly()?.Location ?? throw new InvalidOperationException("Cannot locate the entry assembly."));
            }

            start.ArgumentList.Add(Verb);
            start.ArgumentList.Add(requestPath);

            Process? process;
            try
            {
                process = Process.Start(start);
            }
            catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
            {
                return new ElevatedResponse(request.Id, false, "The administrator prompt was declined.", false);
            }

            using (process)
            {
                if (process is null)
                {
                    return new ElevatedResponse(request.Id, false, "The elevated helper could not be started.", false);
                }

                using var timer = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timer.CancelAfter(timeout);
                try
                {
                    await process.WaitForExitAsync(timer.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    return new ElevatedResponse(request.Id, false, "The elevated helper did not finish in time.", false);
                }
            }

            if (!File.Exists(responsePath))
            {
                return new ElevatedResponse(request.Id, false, "The elevated helper did not answer.", false);
            }

            var response = JsonSerializer.Deserialize<ElevatedResponse>(await File.ReadAllTextAsync(responsePath, cancellationToken).ConfigureAwait(false), Json);
            return response is not null && response.Id == request.Id
                ? response
                : new ElevatedResponse(request.Id, false, "The elevated helper answered a different request.", false);
        }
        finally
        {
            TryDelete(requestPath);
            TryDelete(responsePath);
        }
    }

    /// <summary>
    /// The elevated side. Returns null when <paramref name="arguments"/> are not an elevated invocation; otherwise
    /// validates and runs the request, writes the response, and returns the process exit code.
    /// </summary>
    public static int? TryRunFromCommandLine(IReadOnlyList<string> arguments)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        if (arguments.Count != 2 || arguments[0] != Verb)
        {
            return null;
        }

        var problem = Validate(arguments[1], out var request);
        var response = problem is null
            ? Execute(request!)
            : new ElevatedResponse(request?.Id ?? Guid.Empty, false, problem, IsElevated());
        if (request is not null)
        {
            File.WriteAllText(ResponsePathFor(Path.GetFullPath(arguments[1])), JsonSerializer.Serialize(response, Json), new UTF8Encoding(false));
        }

        return response.Success ? 0 : 1;
    }

    public static bool IsElevated()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }

    /// <summary>
    /// Null when the request may run. The file must be <c>…\SoftColorCalibrator\elevated\&lt;id&gt;.request.json</c> (checked by
    /// name, so it also works when an administrator account other than the user's approves the prompt), its id must match
    /// its name, and its task and arguments must be on the allowlist.
    /// </summary>
    internal static string? Validate(string path, out ElevatedRequest? request)
    {
        request = null;
        string full;
        try
        {
            full = Path.GetFullPath(path);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return "The request path is invalid.";
        }

        var directory = new DirectoryInfo(Path.GetDirectoryName(full) ?? string.Empty);
        if (!directory.Name.Equals("elevated", StringComparison.OrdinalIgnoreCase)
            || directory.Parent?.Name.Equals("SoftColorCalibrator", StringComparison.OrdinalIgnoreCase) != true
            || !full.EndsWith(RequestSuffix, StringComparison.Ordinal))
        {
            return "The request is not in the helper's folder.";
        }

        try
        {
            request = JsonSerializer.Deserialize<ElevatedRequest>(File.ReadAllText(full), Json);
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return "The request is unreadable or names an unknown task.";
        }

        if (request is null || !Enum.IsDefined(request.Task))
        {
            return "Unknown task.";
        }

        if (!Path.GetFileName(full).Equals(request.Id.ToString("N") + RequestSuffix, StringComparison.Ordinal))
        {
            return "The request id does not match its file name.";
        }

        static bool IsBool(IReadOnlyDictionary<string, string> a, string key) => a.TryGetValue(key, out var v) && v is "true" or "false";
        return request.Task switch
        {
            ElevatedTaskKind.Ping or ElevatedTaskKind.TurnOnCalibrationLoader when request.Arguments.Count == 0 => null,
            ElevatedTaskKind.Ping or ElevatedTaskKind.TurnOnCalibrationLoader => $"{request.Task} takes no arguments.",
            ElevatedTaskKind.RestoreCalibrationLoader when request.Arguments.Count == 2 && IsBool(request.Arguments, "switchOn") && IsBool(request.Arguments, "taskEnabled") => null,
            ElevatedTaskKind.RestoreCalibrationLoader => "RestoreCalibrationLoader takes switchOn and taskEnabled (true or false) only.",
            _ => "Unknown task.",
        };
    }

    private static ElevatedResponse Execute(ElevatedRequest request)
    {
        try
        {
            return request.Task switch
            {
                ElevatedTaskKind.Ping => new ElevatedResponse(request.Id, true, IsElevated() ? "pong (elevated)" : "pong (not elevated)", IsElevated()),
                ElevatedTaskKind.TurnOnCalibrationLoader => new ElevatedResponse(request.Id, true, ColorManagement.CalibrationLoader.TurnOn(), IsElevated()),
                ElevatedTaskKind.RestoreCalibrationLoader => new ElevatedResponse(
                    request.Id,
                    true,
                    ColorManagement.CalibrationLoader.Restore(request.Arguments["switchOn"] == "true", request.Arguments["taskEnabled"] == "true"),
                    IsElevated()),
                _ => new ElevatedResponse(request.Id, false, "Unknown task.", IsElevated()),
            };
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
        {
            return new ElevatedResponse(request.Id, false, ex.Message, IsElevated());
        }
    }

    private static string ResponsePathFor(string requestPath) =>
        requestPath[..^RequestSuffix.Length] + ResponseSuffix;

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Left for the next run to overwrite; it holds nothing secret.
        }
    }
}
