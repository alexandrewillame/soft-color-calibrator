using System.Globalization;

namespace SoftCal.FeasibilityHarness.Infrastructure;

/// <summary>Console output; numbers are always formatted with the invariant culture.</summary>
internal static class Ui
{
    private static readonly Lock Gate = new();

    public static void Title(string text) => Write(ConsoleColor.Cyan, $"\n== {text} ==");

    public static void Info(FormattableString text) => Write(null, Format(text));

    public static void Info(string text) => Write(null, text);

    public static void Good(FormattableString text) => Write(ConsoleColor.Green, Format(text));

    public static void Warn(FormattableString text) => Write(ConsoleColor.Yellow, Format(text));

    public static void Warn(string text) => Write(ConsoleColor.Yellow, text);

    public static void Error(string text) => Write(ConsoleColor.Red, text);

    public static string Format(FormattableString text) => text.ToString(CultureInfo.InvariantCulture);

    public static string F(double? value, string format = "0.###") =>
        value is null || double.IsNaN(value.Value) ? "—" : value.Value.ToString(format, CultureInfo.InvariantCulture);

    private static void Write(ConsoleColor? color, string text)
    {
        lock (Gate)
        {
            var previous = Console.ForegroundColor;
            if (color is { } c)
            {
                Console.ForegroundColor = c;
            }

            Console.WriteLine(text);
            Console.ForegroundColor = previous;
        }
    }
}
