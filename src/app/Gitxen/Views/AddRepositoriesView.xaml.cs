// SPDX-License-Identifier: GPL-3.0-or-later
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;

namespace Gitxen.Views;

/// <summary>
///  Home's "Add repositories": the repository finder on its own, without the rest of the setup wizard.
/// </summary>
public sealed partial class AddRepositoriesView : UserControl
{
    private bool _scannedOnce;

    public AddRepositoriesView()
    {
        InitializeComponent();
    }

    /// <summary>Raised with what to add when the user confirms.</summary>
    public event EventHandler<ImportPlan>? Completed;

    /// <summary>Raised when the overlay is closed without adding anything.</summary>
    public event EventHandler? Dismissed;

    /// <summary>Shows the overlay, refreshed with what is on Home now.</summary>
    internal void Show(Func<Task<string?>> pickFolder, IEnumerable<string> knownPaths, IEnumerable<string> projectNames)
    {
        Importer.Initialize(pickFolder, knownPaths, projectNames);
        Visibility = Visibility.Visible;

        // The usual folders are scanned the first time only; later opens keep what was found.
        if (!_scannedOnce)
        {
            _scannedOnce = true;
            Importer.ScanSuggested();
        }

        UpdateAddButton();
    }

    private void Importer_SelectionChanged(object? sender, EventArgs e) => UpdateAddButton();

    private void UpdateAddButton()
    {
        int count = Importer.SelectedCount;
        AddButton.IsEnabled = count > 0;
        AddButton.Content = count switch
        {
            0 => "Add",
            1 => "Add 1 repository",
            _ => $"Add {count} repositories"
        };
    }

    private void Add_Click(object sender, RoutedEventArgs e)
    {
        Importer.CancelScans();
        ImportPlan plan = Importer.BuildPlan();
        Visibility = Visibility.Collapsed;
        Completed?.Invoke(this, plan);
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => Close();

    private void Escape_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        args.Handled = true;
        Close();
    }

    private void Close()
    {
        Importer.CancelScans();
        Visibility = Visibility.Collapsed;
        Dismissed?.Invoke(this, EventArgs.Empty);
    }
}
