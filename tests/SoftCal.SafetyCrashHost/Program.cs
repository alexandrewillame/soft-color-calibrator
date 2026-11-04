using System.Globalization;
using SoftCal.Windows.Safety;

// Test helper for GuardianCrashTests. "run" keeps changing fake devices (files holding a value) under the state journal
// until the test kills it at a random moment; "guardian" is the shared Guardian, started by "run" exactly as the app
// and the harness start theirs.
var journalDirectory = Option(args, "--journal");
if (args.Length > 0 && args[0] == Guardian.Verb)
{
    Guardian.Run(Guardian.ParseParent(args), new StateJournal(journalDirectory), [new FileValueRestorer()], Path.Combine(journalDirectory, "guardian.log"));
    return 0;
}

var devicesDirectory = Option(args, "--devices");
var random = new Random(int.Parse(Option(args, "--seed"), CultureInfo.InvariantCulture));
var journal = new StateJournal(journalDirectory);
Guardian.EnsureStarted(["--journal", journalDirectory]);
var devices = Directory.GetFiles(devicesDirectory, "*.value").Order(StringComparer.Ordinal).ToArray();
var restorer = new FileValueRestorer();
File.WriteAllText(Path.Combine(devicesDirectory, "ready"), Environment.ProcessId.ToString(CultureInfo.InvariantCulture));

while (true)
{
    var device = devices[random.Next(devices.Length)];
    var key = Path.GetFileNameWithoutExtension(device);

    // Journal first, then change: the order every guard in SoftCal follows.
    if (!journal.Contains(FileValueRestorer.Kind, key))
    {
        journal.Save(FileValueRestorer.Kind, key, new FileValue(device, File.ReadAllText(device)));
    }

    File.WriteAllText(device, random.Next(1000, 9999).ToString(CultureInfo.InvariantCulture));

    // Sometimes undo normally, as a guard's Restore does: put the original back, then drop the entry.
    if (random.NextDouble() < 0.25 && journal.Pending(FileValueRestorer.Kind).FirstOrDefault(e => e.Key == key) is { } entry)
    {
        if (restorer.Restore(entry).Outcome == RestoreOutcome.Restored)
        {
            journal.Remove(FileValueRestorer.Kind, key);
        }
    }

    Thread.Sleep(random.Next(1, 4));
}

static string Option(string[] arguments, string name)
{
    var index = Array.IndexOf(arguments, name);
    return index >= 0 && index + 1 < arguments.Length ? arguments[index + 1] : throw new ArgumentException($"Missing {name}.");
}

internal sealed record FileValue(string Path, string Value);

internal sealed class FileValueRestorer : IStateRestorer
{
    public const string Kind = "fake-file";

    string IStateRestorer.Kind => Kind;

    public RestoreResult Restore(JournalEntry entry)
    {
        var original = StateJournal.Read<FileValue>(entry) ?? throw new InvalidDataException("Empty entry.");
        File.WriteAllText(original.Path, original.Value);
        return File.ReadAllText(original.Path) == original.Value
            ? RestoreResult.Restored("value written back")
            : RestoreResult.Failed("value did not read back");
    }
}
