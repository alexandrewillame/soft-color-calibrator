using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using SoftCal.App.Services;
using SoftCal.Core.Analysis;

namespace SoftCal.App;

/// <summary>The full report's before/after view: this report against an earlier one of the same monitor.</summary>
public partial class MainWindow
{
    private DisplayReport? _compareWith;

    /// <summary>The earlier reports of this monitor, newest first; the newest of the same profile is preselected.</summary>
    private void ListEarlierReports(DisplayReport current)
    {
        _compareWith = null;
        var folder = System.IO.Path.Combine(AppPaths.Reports, current.DisplayKey ?? "simulated");
        var earlier = new List<EarlierReport>();
        if (Directory.Exists(folder))
        {
            foreach (var file in Directory.GetFiles(folder, "*.json").Where(f => ReportFile().IsMatch(System.IO.Path.GetFileName(f))).OrderDescending())
            {
                try
                {
                    if (ReportWriter.FromJson(File.ReadAllText(file)) is { } report && report.CreatedUtc != current.CreatedUtc)
                    {
                        earlier.Add(new EarlierReport(report));
                    }
                }
                catch (Exception ex) when (ex is IOException or System.Text.Json.JsonException)
                {
                    // An unreadable or older-format report is simply not offered.
                }
            }
        }

        CompareList.ItemsSource = earlier;
        CompareList.SelectedItem = earlier.FirstOrDefault(r => string.Equals(r.Report.Profile ?? string.Empty, current.Profile ?? string.Empty, StringComparison.OrdinalIgnoreCase))
            ?? earlier.FirstOrDefault();
        if (earlier.Count == 0)
        {
            ComparisonTable.ItemsSource = new[] { new ComparisonItem("No earlier report of this monitor yet: measure again after a change to compare.", string.Empty, string.Empty, string.Empty) };
        }
    }

    private void Compare_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_report is null || CompareList.SelectedItem is not EarlierReport before)
        {
            return;
        }

        _compareWith = before.Report;
        ComparisonTable.ItemsSource = ReportComparison.Compare(before.Report, _report).Select(ToItem).ToList();
        DrawTone();
    }

    /// <summary>The earlier report's tone curve, drawn under this one's in gray.</summary>
    private void DrawEarlierTone(Func<double, double, System.Windows.Point> at)
    {
        if (_compareWith is null)
        {
            return;
        }

        var earlier = new Polyline { Stroke = Brushes.Gray, StrokeThickness = 1.5 };
        earlier.Points.Add(at(0, 0));
        foreach (var p in _compareWith.Tone.Points)
        {
            earlier.Points.Add(at(p.Code, p.Fraction));
        }

        earlier.Points.Add(at(255, 1));
        ToneCanvas.Children.Add(earlier);
    }

    [GeneratedRegex(@"^\d{8}-\d{6}\.json$")]
    private static partial Regex ReportFile();

    /// <summary>An earlier report as listed: when, which profile, and its gamma.</summary>
    internal sealed record EarlierReport(DisplayReport Report)
    {
        public override string ToString() => string.Create(
            CultureInfo.CurrentCulture,
            $"{Report.CreatedUtc.ToLocalTime():yyyy-MM-dd HH:mm}{(string.IsNullOrWhiteSpace(Report.Profile) ? string.Empty : " — " + Report.Profile)} — gamma {Report.Tone.EffectiveGamma:0.00}");
    }

    private static ComparisonItem ToItem(ComparisonRow row) => new(row.Quantity, row.Before, row.After, row.Verdict switch
    {
        ComparisonVerdict.Better => "better ✓",
        ComparisonVerdict.Worse => "worse ✗",
        ComparisonVerdict.Same => "same",
        _ => string.Empty,
    });

    internal sealed record ComparisonItem(string Quantity, string Before, string After, string Verdict);
}
