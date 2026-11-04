using System.Globalization;
using AwesomeAssertions;
using SoftCal.Windows.Elevation;
using Xunit;

namespace SoftCal.Windows.Tests;

public sealed class ElevationTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "softcal-tests", Guid.NewGuid().ToString("N"));

    private string HelperDirectory => Path.Combine(_root, "SoftColorCalibrator", "elevated");

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [Fact]
    public void A_valid_ping_runs_and_writes_its_response()
    {
        var id = Guid.NewGuid();
        var path = Write(id, Request(id, "Ping"));

        var exitCode = ElevatedTasks.TryRunFromCommandLine([ElevatedTasks.Verb, path]);

        exitCode.Should().Be(0);
        File.ReadAllText(Path.Combine(HelperDirectory, $"{id:N}.response.json")).Should().Contain("pong").And.Contain(id.ToString());
    }

    [Fact]
    public void Requests_outside_the_helper_folder_or_with_another_name_are_refused()
    {
        var id = Guid.NewGuid();
        Directory.CreateDirectory(_root);
        var elsewhere = Path.Combine(_root, $"{id:N}.request.json");
        File.WriteAllText(elsewhere, Request(id, "Ping"));
        var wrongSuffix = Write(id, Request(id, "Ping"), $"{id:N}.json");

        ElevatedTasks.Validate(elsewhere, out _).Should().Contain("folder");
        ElevatedTasks.Validate(wrongSuffix, out _).Should().Contain("folder");
    }

    [Fact]
    public void Unknown_tasks_extra_arguments_and_mismatched_ids_are_refused()
    {
        var id = Guid.NewGuid();

        ElevatedTasks.Validate(Write(id, Request(id, "FormatDisk")), out _).Should().Contain("unknown task");
        ElevatedTasks.Validate(Write(id, Request(id, "Ping", """{"cmd":"calc.exe"}""")), out _).Should().Contain("no arguments");
        ElevatedTasks.Validate(Write(id, Request(Guid.NewGuid(), "Ping")), out _).Should().Contain("does not match");
        ElevatedTasks.Validate(Write(id, "{ not json"), out _).Should().Contain("unreadable");
    }

    /// <summary>The calibration loader tasks take exactly their arguments; nothing else gets through. Only Validate runs
    /// here: executing them would change the machine.</summary>
    [Fact]
    public void The_calibration_loader_tasks_accept_only_their_own_arguments()
    {
        var id = Guid.NewGuid();

        ElevatedTasks.Validate(Write(id, Request(id, "TurnOnCalibrationLoader")), out _).Should().BeNull();
        ElevatedTasks.Validate(Write(id, Request(id, "TurnOnCalibrationLoader", """{"task":"other"}""")), out _).Should().Contain("no arguments");
        ElevatedTasks.Validate(Write(id, Request(id, "RestoreCalibrationLoader", """{"switchOn":"false","taskEnabled":"false"}""")), out _).Should().BeNull();
        ElevatedTasks.Validate(Write(id, Request(id, "RestoreCalibrationLoader", """{"switchOn":"maybe","taskEnabled":"false"}""")), out _).Should().Contain("true or false");
        ElevatedTasks.Validate(Write(id, Request(id, "RestoreCalibrationLoader", """{"switchOn":"true","taskEnabled":"true","cmd":"x"}""")), out _).Should().Contain("only");
    }

    [Fact]
    public void Other_command_lines_are_not_elevated_invocations()
    {
        ElevatedTasks.TryRunFromCommandLine([]).Should().BeNull();
        ElevatedTasks.TryRunFromCommandLine(["probe"]).Should().BeNull();
        ElevatedTasks.TryRunFromCommandLine([ElevatedTasks.Verb]).Should().BeNull();
    }

    private static string Request(Guid id, string task, string arguments = "{}") =>
        string.Create(CultureInfo.InvariantCulture, $"{{\"id\":\"{id}\",\"task\":\"{task}\",\"arguments\":{arguments}}}");

    private string Write(Guid id, string json, string? fileName = null)
    {
        Directory.CreateDirectory(HelperDirectory);
        var path = Path.Combine(HelperDirectory, fileName ?? $"{id:N}.request.json");
        File.WriteAllText(path, json);
        return path;
    }
}
