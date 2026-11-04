using System.Windows;
using System.Windows.Controls;
using SoftCal.App.Services;

namespace SoftCal.App;

/// <summary>Asks a profile's name: for a new one, or a new name for one.
/// The name is checked as it is typed: not empty, short enough, not another profile's.</summary>
public partial class ProfileNameDialog : Window
{
    private readonly IReadOnlyList<string> _taken;
    private readonly string? _except;

    private ProfileNameDialog(IReadOnlyList<string> taken, string? except)
    {
        InitializeComponent();
        _taken = taken;
        _except = except;
        Loaded += (_, _) =>
        {
            NameBox.Focus();
            NameBox.SelectAll();
        };
    }

    /// <summary>The name typed (trimmed), once OK was clicked.</summary>
    public string ProfileName => NameBox.Text.Trim();

    /// <summary>A new profile's name (null: cancelled); it starts from the current monitor settings.</summary>
    public static string? AskNew(Window owner, IReadOnlyList<string> taken, string suggested)
    {
        var dialog = new ProfileNameDialog(taken, null) { Owner = owner, Title = "New profile" };
        dialog.Explanation.Text = "It starts from the current monitor settings.";
        dialog.NameBox.Text = suggested;
        return dialog.ShowDialog() == true ? dialog.ProfileName : null;
    }

    /// <summary>A new name for profile <paramref name="current"/> (null: cancelled).</summary>
    public static string? AskRename(Window owner, IReadOnlyList<string> taken, string current)
    {
        var dialog = new ProfileNameDialog(taken, current) { Owner = owner, Title = "Rename profile" };
        dialog.Heading.Text = "Rename profile";
        dialog.Explanation.Text = "Earlier measurements keep the old name.";
        dialog.NameBox.Text = current;
        dialog.OkButton.Content = "Rename";
        return dialog.ShowDialog() == true ? dialog.ProfileName : null;
    }

    private string? Check() => MonitorProfileStore.CheckName(NameBox.Text, _taken, _except);

    private void NameBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        var problem = Check();
        Problem.Text = NameBox.Text.Trim().Length == 0 ? string.Empty : problem ?? string.Empty;
        OkButton.IsEnabled = problem is null;
    }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        if (Check() is { } problem)
        {
            Problem.Text = problem;
            return;
        }

        DialogResult = true;
    }
}
