using System.Globalization;

namespace SoftCal.FeasibilityHarness.Infrastructure;

/// <summary>Command line: a command, positional words, <c>--key value</c> options and <c>--flag</c> switches.</summary>
internal sealed class Arguments
{
    private readonly Dictionary<string, string> _options = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _flags = new(StringComparer.OrdinalIgnoreCase);

    private Arguments(string command, IReadOnlyList<string> positional)
    {
        Command = command;
        Positional = positional;
    }

    public string Command { get; }

    public IReadOnlyList<string> Positional { get; }

    /// <summary>Options that take a value; anything else starting with -- is a flag.</summary>
    private static readonly HashSet<string> ValueOptions = new(StringComparer.OrdinalIgnoreCase)
    {
        "monitor", "camera", "runs", "repeats", "phase", "minutes", "focus", "sessions", "out", "parent", "seed", "levels", "frames", "exposure", "stop-code", "steps", "geometry", "anchors", "ramp-runs",
    };

    public static Arguments Parse(IReadOnlyList<string> args)
    {
        ArgumentNullException.ThrowIfNull(args);
        var positional = new List<string>();
        string? command = null;
        var parsed = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var flags = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < args.Count; i++)
        {
            var a = args[i];
            if (a.StartsWith("--", StringComparison.Ordinal))
            {
                var key = a[2..];
                var eq = key.IndexOf('=', StringComparison.Ordinal);
                if (eq > 0)
                {
                    parsed[key[..eq]] = key[(eq + 1)..];
                }
                else if (ValueOptions.Contains(key) && i + 1 < args.Count)
                {
                    parsed[key] = args[++i];
                }
                else
                {
                    flags.Add(key);
                }
            }
            else if (command is null)
            {
                command = a;
            }
            else
            {
                positional.Add(a);
            }
        }

        var result = new Arguments(command ?? "help", positional);
        foreach (var kv in parsed)
        {
            result._options[kv.Key] = kv.Value;
        }

        result._flags.UnionWith(flags);
        return result;
    }

    public string? Get(string key) => _options.TryGetValue(key, out var v) ? v : null;

    public int GetInt(string key, int fallback) =>
        Get(key) is { } v ? int.Parse(v, NumberStyles.Integer, CultureInfo.InvariantCulture) : fallback;

    public double GetDouble(string key, double fallback) =>
        Get(key) is { } v ? double.Parse(v, NumberStyles.Float, CultureInfo.InvariantCulture) : fallback;

    public bool Has(string flag) => _flags.Contains(flag);
}
