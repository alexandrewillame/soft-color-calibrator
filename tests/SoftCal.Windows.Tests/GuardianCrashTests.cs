using System.Diagnostics;
using System.Globalization;
using AwesomeAssertions;
using SoftCal.Windows.Safety;
using Xunit;

namespace SoftCal.Windows.Tests;

/// <summary>
/// M2's exit criterion: killing the app at a random moment restores every kind of state. A helper process changes six
/// fake devices under the state journal, exactly as the guards do, and is killed (TerminateProcess: no finally blocks,
/// no cleanup) at a random time; the shared guardian it started must then put every original value back.
/// </summary>
public sealed class GuardianCrashTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "softcal-crash", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The guardian may still be appending to its log.
        }
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    public async Task Killing_the_process_at_a_random_moment_restores_every_device(int seed)
    {
        var token = TestContext.Current.CancellationToken;
        var journalDirectory = Path.Combine(_root, "journal");
        var devicesDirectory = Path.Combine(_root, "devices");
        Directory.CreateDirectory(devicesDirectory);
        var originals = Enumerable.Range(0, 6).ToDictionary(
            i => Path.Combine(devicesDirectory, string.Create(CultureInfo.InvariantCulture, $"device{i}.value")),
            i => string.Create(CultureInfo.InvariantCulture, $"original-{i}"));
        foreach (var (path, value) in originals)
        {
            await File.WriteAllTextAsync(path, value, token);
        }

        var start = new ProcessStartInfo(Path.Combine(AppContext.BaseDirectory, "SoftCal.SafetyCrashHost.exe"))
        {
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var argument in new[] { "run", "--journal", journalDirectory, "--devices", devicesDirectory, "--seed", seed.ToString(CultureInfo.InvariantCulture) })
        {
            start.ArgumentList.Add(argument);
        }

        using var host = Process.Start(start)!;
        await WaitUntilAsync(() => File.Exists(Path.Combine(devicesDirectory, "ready")), TimeSpan.FromSeconds(30), token);
        await Task.Delay(new Random(seed).Next(20, 400), token);

        host.Kill();
        await host.WaitForExitAsync(token);

        var journal = new StateJournal(journalDirectory);
        await WaitUntilAsync(() => journal.Pending().Count == 0, TimeSpan.FromSeconds(30), token);
        foreach (var (path, value) in originals)
        {
            (await File.ReadAllTextAsync(path, token)).Should().Be(value, $"{Path.GetFileName(path)} must be back to its original");
        }
    }

    private static async Task WaitUntilAsync(Func<bool> condition, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var watch = Stopwatch.StartNew();
        while (!condition())
        {
            if (watch.Elapsed > timeout)
            {
                throw new TimeoutException($"Condition not met within {timeout.TotalSeconds:0} s.");
            }

            await Task.Delay(50, cancellationToken);
        }
    }
}
