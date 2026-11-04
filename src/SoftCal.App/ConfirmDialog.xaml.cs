using System.Windows;
using System.Windows.Controls;

namespace SoftCal.App;

/// <summary>Asks before an action that overwrites settings (saving into a profile, loading one onto the monitor,
/// deleting one): what it changes, line by line, and its button under the action's own name. Cancel is the default, so
/// Enter alone changes nothing.</summary>
public partial class ConfirmDialog : Window
{
    private ConfirmDialog()
    {
        InitializeComponent();
        Loaded += (_, _) => CancelButton.Focus();
    }

    /// <summary>True when the user clicked <paramref name="action"/>.</summary>
    public static bool Ask(Window owner, string title, string heading, IReadOnlyList<string> changes, string action, string? note = null, string? warning = null)
    {
        ArgumentNullException.ThrowIfNull(changes);
        var dialog = new ConfirmDialog { Owner = owner, Title = title };
        dialog.Heading.Text = heading;
        dialog.Changes.ItemsSource = changes;
        dialog.Changes.Visibility = changes.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        Show(dialog.Note, note);
        Show(dialog.Warning, warning);
        dialog.OkButton.Content = action;
        return dialog.ShowDialog() == true;
    }

    private static void Show(TextBlock block, string? text)
    {
        block.Text = text ?? string.Empty;
        block.Visibility = string.IsNullOrEmpty(text) ? Visibility.Collapsed : Visibility.Visible;
    }

    private void Ok_Click(object sender, RoutedEventArgs e) => DialogResult = true;
}
