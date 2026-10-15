// SPDX-License-Identifier: GPL-3.0-or-later
using Gitxen.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.ApplicationModel.DataTransfer;

namespace Gitxen.Views;

/// <summary>
///  The commits that changed one file, newest first. Shown in the History page's lower pane; the host
///  owns the <see cref="FileHistoryViewModel"/> and decides when to read it.
/// </summary>
public sealed partial class FileHistoryPanel : UserControl
{
    public static readonly DependencyProperty ModelProperty = DependencyProperty.Register(
        nameof(Model), typeof(FileHistoryViewModel), typeof(FileHistoryPanel), new PropertyMetadata(null));

    public FileHistoryPanel()
    {
        InitializeComponent();
    }

    public FileHistoryViewModel? Model
    {
        get => (FileHistoryViewModel?)GetValue(ModelProperty);
        set => SetValue(ModelProperty, value);
    }

    /// <summary>Right-click selects the row first, so the menu acts on the commit that was clicked.</summary>
    private void Row_RightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        if (Model is FileHistoryViewModel model && sender is FrameworkElement { DataContext: FileHistoryRow row })
        {
            model.Selected = row;
        }
    }

    private void Row_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        if (Model is FileHistoryViewModel model && sender is FrameworkElement { DataContext: FileHistoryRow row })
        {
            model.Selected = row;
            model.ShowSelected();
        }
    }

    private void ShowInHistory_Click(object sender, RoutedEventArgs e) => Model?.ShowSelected();

    private void CopyHash_Click(object sender, RoutedEventArgs e)
    {
        if (Model?.Selected is FileHistoryRow row)
        {
            DataPackage package = new();
            package.SetText(row.Commit.Id);
            Clipboard.SetContent(package);
        }
    }
}
