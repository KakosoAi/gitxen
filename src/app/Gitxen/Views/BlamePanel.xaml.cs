// SPDX-License-Identifier: GPL-3.0-or-later
using Gitxen.Git;
using Gitxen.Services;
using Gitxen.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.ApplicationModel.DataTransfer;

namespace Gitxen.Views;

/// <summary>
///  Blame for one file: every line with the commit that last changed it, named once per run of lines.
/// </summary>
/// <remarks>
///  Shown in the History page's lower pane. Like <see cref="DiffPanel"/> it does no git work of its
///  own — the host owns the <see cref="BlameViewModel"/> and decides when to read it.
/// </remarks>
public sealed partial class BlamePanel : UserControl
{
    public static readonly DependencyProperty ModelProperty = DependencyProperty.Register(
        nameof(Model), typeof(BlameViewModel), typeof(BlamePanel), new PropertyMetadata(null));

    public BlamePanel()
    {
        InitializeComponent();
    }

    public BlameViewModel? Model
    {
        get => (BlameViewModel?)GetValue(ModelProperty);
        set => SetValue(ModelProperty, value);
    }

    /// <summary>Right-click selects the row first, so the menu acts on the line that was clicked.</summary>
    private void Row_RightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        if (Model is BlameViewModel model && sender is FrameworkElement { DataContext: BlameRow row })
        {
            model.Selected = row;
        }
    }

    private void Row_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        if (Model is BlameViewModel model && sender is FrameworkElement { DataContext: BlameRow row })
        {
            model.Selected = row;
            model.ShowCommit(row);
        }
    }

    private void ShowCommit_Click(object sender, RoutedEventArgs e) => Model?.ShowCommit(Model.Selected);

    /// <summary>
    ///  Every commit that changed this line, newest first, each with what it did to the line — "who
    ///  wrote this, and what was it before". A commit's header opens it in History.
    /// </summary>
    private async void LineHistory_Click(object sender, RoutedEventArgs e)
    {
        if (Model is not BlameViewModel model || model.Selected is not BlameRow row)
        {
            return;
        }

        if (!row.CanShowCommit)
        {
            return;
        }

        StackPanel list = new() { Spacing = 14 };
        list.Children.Add(new TextBlock { Text = "Reading the line's history…", Opacity = 0.7 });

        ContentDialog dialog = new()
        {
            Title = $"Line {row.LineNumber} of {model.Title}",
            Content = new ScrollViewer { Content = list, MaxHeight = 520, Width = 640, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollMode = ScrollMode.Auto },
            CloseButtonText = "Close",
            XamlRoot = XamlRoot
        };

        Task<ContentDialogResult> shown = dialog.ShowAsync().AsTask();
        IReadOnlyList<GitLineChange> changes = await model.ReadLineHistoryAsync(row);
        list.Children.Clear();

        if (changes.Count == 0)
        {
            list.Children.Add(new TextBlock { Text = "git found no history for this line.", Opacity = 0.7 });
        }

        foreach (GitLineChange change in changes)
        {
            HyperlinkButton header = new()
            {
                Content = $"{change.ShortId}  {change.Subject}",
                Padding = new Thickness(0),
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold
            };
            ToolTipService.SetToolTip(header, "Show this commit in history");
            header.Click += (_, _) =>
            {
                dialog.Hide();
                model.ShowCommit(change.CommitId);
            };

            StackPanel entry = new() { Spacing = 4 };
            entry.Children.Add(header);
            entry.Children.Add(new TextBlock
            {
                Text = $"{change.Author} · {TimeText.Relative(change.Date)}",
                FontSize = 12,
                Opacity = 0.7
            });
            entry.Children.Add(new TextBlock
            {
                Text = change.Patch.Length > 0 ? change.Patch : "(a merge that brought the line in, with no change of its own)",
                FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Cascadia Mono, Consolas"),
                FontSize = 12,
                IsTextSelectionEnabled = true,
                Opacity = change.Patch.Length > 0 ? 1 : 0.6
            });
            list.Children.Add(entry);
        }

        await shown;
    }

    private void CopyHash_Click(object sender, RoutedEventArgs e)
    {
        if (Model?.Selected is { CanShowCommit: true } row)
        {
            DataPackage package = new();
            package.SetText(row.Line.CommitId);
            Clipboard.SetContent(package);
        }
    }
}
