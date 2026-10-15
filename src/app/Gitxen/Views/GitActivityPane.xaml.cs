// SPDX-License-Identifier: GPL-3.0-or-later
using Gitxen.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Gitxen.Views;

/// <summary>
///  A console for what the app is actually doing: every process it launches, live, with the
///  selected one's full detail — arguments, working directory, duration, exit code, captured error.
/// </summary>
/// <remarks>
///  Fed by <see cref="GitActivity"/>, which <see cref="Gitxen.Git.GitCli"/> reports to as each
///  command finishes, so every process the app launches is here without any page instrumenting
///  itself. Standard output is deliberately not kept — a <c>git log</c> can produce megabytes — so
///  output is shown where it is consumed: the pages, and each operation's InfoBar.
/// </remarks>
public sealed partial class GitActivityPane : UserControl
{
    /// <summary>Coalesces change bursts: one queued refresh at a time, however many events arrive.</summary>
    private bool _refreshQueued;

    public GitActivityPane()
    {
        InitializeComponent();
    }

    public void Toggle()
    {
        if (Visibility == Visibility.Visible)
        {
            Hide();
        }
        else
        {
            Show();
        }
    }

    /// <summary>Shows the pane and starts following the log. Subscribed only while visible.</summary>
    public void Show()
    {
        Visibility = Visibility.Visible;

        GitActivity.Changed -= OnCommandsChanged;
        GitActivity.Changed += OnCommandsChanged;

        Refresh();
    }

    public void Hide()
    {
        Visibility = Visibility.Collapsed;
        GitActivity.Changed -= OnCommandsChanged;
    }

    /// <summary>Raised from whichever thread ran the process; the refresh belongs to the UI thread.</summary>
    private void OnCommandsChanged()
    {
        if (_refreshQueued)
        {
            return;
        }

        _refreshQueued = true;

        DispatcherQueue.TryEnqueue(() =>
        {
            _refreshQueued = false;
            Refresh();
        });
    }

    /// <summary>
    ///  Re-reads the log. The list is reassigned rather than updated in place: entries mutate as
    ///  their processes finish (duration, exit code), and rebinding is what re-renders their rows.
    /// </summary>
    private void Refresh()
    {
        // Sorted on ColumnLine, which begins with the start time, so a command still running when
        // another finishes stays in the order it was launched.
        string? selected = (List.SelectedItem as GitActivityRow)?.ColumnLine;

        List<GitActivityRow> entries =
        [
            .. GitActivity.Entries.Select(static entry =>
                new GitActivityRow(entry.ColumnLine, entry.CommandLine, entry.Detail))
        ];

        entries.Sort(static (left, right) => string.CompareOrdinal(left.ColumnLine, right.ColumnLine));
        List.ItemsSource = entries;

        GitActivityRow? previous = selected is null
            ? null
            : entries.Find(entry => entry.ColumnLine == selected);

        if (previous is not null)
        {
            List.SelectedItem = previous;
        }
        else if (entries.Count > 0)
        {
            // Following the newest, like a console does, until a row is being inspected.
            List.ScrollIntoView(entries[^1]);
        }
    }

    private void List_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (List.SelectedItem is GitActivityRow entry)
        {
            DetailBox.Text = entry.Detail;
            DetailBox.Visibility = Visibility.Visible;
        }
        else
        {
            DetailBox.Visibility = Visibility.Collapsed;
        }
    }

    private void Clear_Click(object sender, RoutedEventArgs e)
    {
        GitActivity.Clear();
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Hide();
}
