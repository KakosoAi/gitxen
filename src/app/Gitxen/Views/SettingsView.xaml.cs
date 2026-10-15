// SPDX-License-Identifier: GPL-3.0-or-later
using System.Diagnostics;
using Gitxen.Git;
using Gitxen.GitHub;
using Gitxen.Graph;
using Gitxen.Services;
using Gitxen.Theming;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace Gitxen.Views;

/// <summary>
///  Application settings, as a page.
/// </summary>
/// <remarks>
///  <para>
///   Previously a modal dialog built by hand in code. As a page each setting can carry a sentence
///   explaining what it costs, which matters for the two limits — they exist for a reason and the
///   number alone does not say what it is.
///  </para>
///  <para>
///   Derives from <see cref="RepositoryPage"/> for uniformity with the other sections even though it
///   ignores <see cref="RepositoryPage.Tab"/>: these settings are process-wide, not per repository.
///  </para>
/// </remarks>
public sealed partial class SettingsView : RepositoryPage
{
    /// <summary>
    ///  Suppresses the change handlers while the controls are being populated from current state, so
    ///  loading the page does not look like the user editing it.
    /// </summary>
    private bool _isLoading;

    public SettingsView()
    {
        InitializeComponent();
    }

    /// <summary>"Gitxen 0.2.0", from the assembly, so the card cannot disagree with the build.</summary>
    public string VersionText
    {
        get
        {
            Version? version = typeof(SettingsView).Assembly.GetName().Version;
            return version is null ? "Gitxen" : $"Gitxen {version.ToString(3)}";
        }
    }

    public override Task ActivateAsync()
    {
        _isLoading = true;

        IReadOnlyList<ThemeOption> themes = ThemeOption.Build(ThemeService.IsSystemDark);
        ThemeGallery.ItemsSource = themes;
        ThemeGallery.SelectedItem = themes.FirstOrDefault(option => option.Theme == AppOptions.Theme)
            ?? themes[0];
        CommitsBox.Value = AppOptions.MaxCommits;
        DiffLinesBox.Value = AppOptions.MaxDiffLines;
        DensityBox.SelectedIndex = AppOptions.Density == UiDensity.Compact ? 1 : 0;
        GraphStyleBox.SelectedIndex = (int)AppOptions.GraphStyle;
        GraphWeightBox.SelectedIndex = (int)AppOptions.GraphWeight;
        GraphSpacingBox.SelectedIndex = (int)AppOptions.GraphSpacing;
        GraphColourBox.SelectedIndex = AppOptions.GraphSingleColour ? 1 : 0;
        DrawGraphPreview();
        StartScreenToggle.IsOn = AppOptions.StartWithLauncher;
        LayoutBox.SelectedIndex = AppOptions.Layout == RepositoryLayout.Sidebar ? 1 : 0;
        TabBranchBox.IsChecked = AppOptions.TabShowBranch;
        TabChangesBox.IsChecked = AppOptions.TabShowChanges;
        TabAheadBehindBox.IsChecked = AppOptions.TabShowAheadBehind;
        GitHubToggle.IsOn = AppOptions.GitHubEnabled;
        AiKeyStatus.Text = AiAssistant.KeySource;
        _ = ShowIndexStatusAsync();
        GhPathBox.Text = AppOptions.GhPath;
        GitHubListBox.Value = AppOptions.GitHubListSize;

        _isLoading = false;

        // Not awaited: the answer arrives when gh has given it, and the rest of the page is usable
        // in the meantime.
        _ = ShowGitHubStatusAsync(refresh: false);
        return Task.CompletedTask;
    }

    // ---- GitHub ---------------------------------------------------------------------------------

    private async Task ShowGitHubStatusAsync(bool refresh)
    {
        GitHubChecking.IsActive = true;
        GitHubStatusText.Text = "Checking the GitHub CLI…";

        GhStatus status = await GitHubService.GetStatusAsync(refresh);

        GitHubChecking.IsActive = false;

        GitHubStatusText.Text = status.Availability switch
        {
            GhAvailability.Ready => status.Accounts.Count > 1
                ? $"gh {status.Version} · signed in as {string.Join(", ", status.Accounts)} ({status.Login} active). Each repository uses the account that can read it; choose another from its GitHub pages."
                : $"gh {status.Version} · signed in as {status.Login}",
            GhAvailability.NotSignedIn => $"gh {status.Version} is installed but not signed in to github.com.",
            _ => "The GitHub CLI was not found. Install it, or say where it is above."
        };

        GitHubSignIn.Visibility = status.Availability == GhAvailability.NotSignedIn ? Visibility.Visible : Visibility.Collapsed;
        GitHubGet.Visibility = status.Availability == GhAvailability.NotInstalled ? Visibility.Visible : Visibility.Collapsed;
    }

    private void GitHubToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (_isLoading)
        {
            return;
        }

        AppOptions.GitHubEnabled = GitHubToggle.IsOn;
        GitHubService.NotifySettingsChanged();
    }

    /// <summary>Committed when the box loses focus, not per keystroke, so gh is not re-checked for every letter.</summary>
    private async void GhPath_LostFocus(object sender, RoutedEventArgs e)
    {
        string path = GhPathBox.Text.Trim();

        if (_isLoading || path == AppOptions.GhPath)
        {
            return;
        }

        AppOptions.GhPath = path;
        GitHubService.NotifySettingsChanged();
        await ShowGitHubStatusAsync(refresh: true);
    }

    private void GitHubList_ValueChanged(NumberBox sender, NumberBoxValueChangedEventArgs args)
    {
        if (!_isLoading && !double.IsNaN(args.NewValue))
        {
            AppOptions.GitHubListSize = (int)args.NewValue;
        }
    }

    private async void GitHubCheck_Click(object sender, RoutedEventArgs e)
    {
        // Also tells the open repositories: after signing in, the pages that were saying gh is not
        // signed in are stale, and this is the moment that is known.
        GitHubService.NotifySettingsChanged();
        await ShowGitHubStatusAsync(refresh: true);
    }

    /// <summary>
    ///  Opens gh's own sign-in in a console window.
    /// </summary>
    /// <remarks>
    ///  Deliberately gh's flow rather than one of ours: it opens the browser, shows the one-time code,
    ///  and stores the token in the Windows credential store, and none of that passes through this
    ///  application. <c>cmd /k</c> keeps the window open afterwards so a failure can be read.
    /// </remarks>
    private void GitHubSignIn_Click(object sender, RoutedEventArgs e)
    {
        string gh = string.IsNullOrWhiteSpace(AppOptions.GhPath) ? "gh" : AppOptions.GhPath;

        try
        {
            Process.Start(new ProcessStartInfo("cmd.exe", "/k \"\"" + gh + "\" auth login --hostname github.com --web\"")
            {
                UseShellExecute = true
            });
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            GitHubStatusText.Text = $"Could not start gh: {ex.Message}";
        }
    }

    /// <summary>Through the shell's view model, which is what shows or hides the strip and the column.</summary>
    private void Layout_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isLoading || LayoutBox.SelectedIndex < 0 || App.Shell is not MainWindow window)
        {
            return;
        }

        window.ViewModel.Layout = LayoutBox.SelectedIndex == 1 ? RepositoryLayout.Sidebar : RepositoryLayout.Tabs;
    }

    private void StartScreen_Toggled(object sender, RoutedEventArgs e)
    {
        if (!_isLoading)
        {
            AppOptions.StartWithLauncher = StartScreenToggle.IsOn;
        }
    }

    /// <summary>Takes effect at once: every open tab redraws its header.</summary>
    private async Task ShowIndexStatusAsync()
    {
        IndexStatus status = await SearchIndexer.StatusAsync();

        IndexStatusText.Text = status.Commits == 0
            ? "Nothing is indexed yet. It fills in the background after Gitxen starts."
            : $"{status.Commits:N0} commits from {status.Repositories:N0} {(status.Repositories == 1 ? "repository" : "repositories")}, "
                + $"{Gitxen.ViewModels.FileEntryRow.FormatSize(status.Bytes)} on disk at {RepositoryIndex.DefaultPath}.";
    }

    private async void RebuildIndex_Click(object sender, RoutedEventArgs e)
    {
        IndexStatusText.Text = "Rebuilding in the background…";

        // The open repositories now; the rest are picked up the next time Gitxen starts.
        string[] paths = [.. SearchIndexer.KnownPaths(Tab is { } tab ? [tab.WorkingDir] : [], [], [])];
        await SearchIndexer.RebuildAsync(paths);
    }

    private async void ClearIndex_Click(object sender, RoutedEventArgs e)
    {
        await SearchIndexer.ClearAsync();
        await ShowIndexStatusAsync();
    }

    /// <summary>Stores the key in Windows Credential Manager, never in Gitxen's own files, and clears the box.</summary>
    private void SaveAiKey_Click(object sender, RoutedEventArgs e)
    {
        string key = AiKeyBox.Password.Trim();

        if (key.Length == 0)
        {
            AiKeyStatus.Text = "Paste a key first. Keys start with sk-ant-.";
            return;
        }

        AiAssistant.SaveKey(key);
        AiKeyBox.Password = "";
        AiKeyStatus.Text = AiAssistant.KeySource;
    }

    private void RemoveAiKey_Click(object sender, RoutedEventArgs e)
    {
        AiAssistant.ForgetKey();
        AiKeyStatus.Text = AiAssistant.KeySource;
    }

    private void TabDetails_Click(object sender, RoutedEventArgs e)
    {
        AppOptions.TabShowBranch = TabBranchBox.IsChecked == true;
        AppOptions.TabShowChanges = TabChangesBox.IsChecked == true;
        AppOptions.TabShowAheadBehind = TabAheadBehindBox.IsChecked == true;
        AppOptions.NotifyTabDetailsChanged();
    }

    // ---- Commit graph ---------------------------------------------------------------------------

    private void Graph_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isLoading)
        {
            return;
        }

        AppOptions.GraphStyle = (GraphLineStyle)Math.Max(0, GraphStyleBox.SelectedIndex);
        AppOptions.GraphWeight = (GraphLineWeight)Math.Max(0, GraphWeightBox.SelectedIndex);
        AppOptions.GraphSpacing = (GraphLaneSpacing)Math.Max(0, GraphSpacingBox.SelectedIndex);
        AppOptions.GraphSingleColour = GraphColourBox.SelectedIndex == 1;

        AppOptions.NotifyGraphChanged();
        DrawGraphPreview();
    }

    /// <summary>
    ///  A small made-up history drawn by the History page's own graph code: a feature branch merged
    ///  back, and a second branch still open, all grown from one base.
    /// </summary>
    private void DrawGraphPreview()
    {
        (string Id, string[] Parents, string Subject)[] history =
        [
            ("m1", ["c1", "f1"], "Merge branch 'feature'"),
            ("f1", ["f2"], "Finish the feature"),
            ("h1", ["c2"], "Try an idea on another branch"),
            ("c1", ["c2"], "Fix a typo on main"),
            ("f2", ["c2"], "Start the feature"),
            ("c2", ["c3"], "Shared base"),
            ("c3", [], "First commit")
        ];

        CommitGraphBuilder builder = new();
        double rowHeight = CommitGraphBuilder.RowHeight;
        double width = CommitGraphBuilder.ColumnWidth;

        GraphPreview.Children.Clear();

        for (int index = 0; index < history.Length; index++)
        {
            (string id, string[] parents, string subject) = history[index];
            GraphRow row = builder.AddCommit(new GitCommit(id, parents, "", "", DateTime.Now, subject, ""));

            foreach (GraphSegment segment in row.Segments)
            {
                Microsoft.UI.Xaml.Shapes.Path path = new()
                {
                    Data = segment.Geometry,
                    Stroke = segment.Stroke,
                    Fill = segment.Fill,
                    StrokeThickness = segment.Thickness,
                    StrokeStartLineCap = PenLineCap.Round,
                    StrokeEndLineCap = PenLineCap.Round,
                    StrokeLineJoin = PenLineJoin.Round
                };

                Canvas.SetTop(path, index * rowHeight);
                GraphPreview.Children.Add(path);
            }

            TextBlock text = new()
            {
                Text = subject,
                Style = (Style)Application.Current.Resources["SecondaryTextStyle"]
            };

            Canvas.SetLeft(text, width + 4);
            Canvas.SetTop(text, (index * rowHeight) + ((rowHeight - 19) / 2));
            GraphPreview.Children.Add(text);
        }

        GraphPreview.Width = width + 240;
        GraphPreview.Height = history.Length * rowHeight;
    }

    private void Density_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isLoading)
        {
            return;
        }

        AppOptions.Density = DensityBox.SelectedIndex == 1 ? UiDensity.Compact : UiDensity.Comfortable;

        // Replaces the shared row style for pages parsed from here on; the commit graph picks the
        // new row height up as each list refreshes.
        DensityService.Apply();
    }

    private void Theme_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        // Also raised while the gallery is being populated, and when a selection is cleared.
        if (_isLoading || ThemeGallery.SelectedItem is not ThemeOption option)
        {
            return;
        }

        AppOptions.Theme = option.Theme;
        ThemeService.Apply();
    }

    private void Commits_ValueChanged(NumberBox sender, NumberBoxValueChangedEventArgs args)
    {
        // NaN arrives when the box is cleared mid-edit; leave the stored value alone until it is valid.
        if (!_isLoading && !double.IsNaN(args.NewValue))
        {
            AppOptions.MaxCommits = (int)args.NewValue;
        }
    }

    private void DiffLines_ValueChanged(NumberBox sender, NumberBoxValueChangedEventArgs args)
    {
        if (!_isLoading && !double.IsNaN(args.NewValue))
        {
            AppOptions.MaxDiffLines = (int)args.NewValue;
        }
    }

    /// <summary>
    ///  Resets the application to a first run: confirm, wipe the stored state, relaunch, close.
    /// </summary>
    /// <remarks>
    ///  The new process is started before the window closes. It launches into an already-emptied
    ///  state directory, and <see cref="SessionStore.Reset"/> has disarmed <c>Save</c>, so the closing
    ///  window's final save cannot write the old session back in between.
    /// </remarks>
    private async void Reset_Click(object sender, RoutedEventArgs e)
    {
        ContentDialog dialog = new()
        {
            Title = "Reset Gitxen?",
            Content = new TextBlock
            {
                Text = "This closes every tab and clears projects, recent repositories, layout and "
                    + "all settings. Gitxen restarts as a first run.\n\n"
                    + "Your repositories on disk and your git configuration are not touched.",
                TextWrapping = TextWrapping.Wrap
            },
            PrimaryButtonText = "Reset and restart",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = XamlRoot
        };

        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
        {
            return;
        }

        SessionStore.Reset();

        if (Environment.ProcessPath is string executable)
        {
            Process.Start(new ProcessStartInfo(executable) { UseShellExecute = true });
        }

        // Through the window rather than Application.Exit, so the Closed handler still runs — it is
        // what shuts the terminal shells down.
        App.Shell?.Close();
    }
}
