// SPDX-License-Identifier: GPL-3.0-or-later
using Gitxen.Git;
using Gitxen.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;

namespace Gitxen.Views;

/// <summary>
///  The window's status bar: branch, divergence, pending changes and repository state in one glance,
///  in the accent colour so it reads as chrome rather than content.
/// </summary>
/// <remarks>
///  Every segment is a shortcut as well as a readout — branch to Branches, changes to Changes, the
///  sync counts to a fetch — because a status bar whose facts cannot be acted on is just a caption.
///  While Home is selected there is no repository to report, so the bar shows the application's own
///  facts instead; only Zen hides it, with the rest of the chrome.
/// </remarks>
public sealed partial class StatusBar : UserControl
{
    public static readonly DependencyProperty TabProperty = DependencyProperty.Register(
        nameof(Tab),
        typeof(RepositoryTabViewModel),
        typeof(StatusBar),
        new PropertyMetadata(null, (sender, _) => ((StatusBar)sender).ShowSide()));

    public static readonly DependencyProperty ShellProperty = DependencyProperty.Register(
        nameof(Shell),
        typeof(MainViewModel),
        typeof(StatusBar),
        new PropertyMetadata(null));

    public StatusBar()
    {
        InitializeComponent();
        Loaded += StatusBar_Loaded;
    }

    /// <summary>The repository whose state the bar shows. Null while Home is selected.</summary>
    public RepositoryTabViewModel? Tab
    {
        get => (RepositoryTabViewModel?)GetValue(TabProperty);
        set => SetValue(TabProperty, value);
    }

    /// <summary>The window's view model, for the facts the Home side of the bar reports.</summary>
    public MainViewModel? Shell
    {
        get => (MainViewModel?)GetValue(ShellProperty);
        set => SetValue(ShellProperty, value);
    }

    /// <summary>
    ///  Swaps the two sides of the bar on the one fact that separates them: is a repository selected.
    /// </summary>
    /// <remarks>
    ///  Set here rather than bound. A bound function of <see cref="Tab"/> was not re-run when the tab
    ///  became null — going Home, or closing the last repository — so the closed repository's branch
    ///  and counts stayed on the bar over Home.
    /// </remarks>
    private void ShowSide()
    {
        bool repository = Tab is not null;

        HomeFacts.Visibility = repository ? Visibility.Collapsed : Visibility.Visible;
        HomeSummary.Visibility = repository ? Visibility.Collapsed : Visibility.Visible;
        RepositoryFacts.Visibility = repository ? Visibility.Visible : Visibility.Collapsed;
        RepositoryStatus.Visibility = repository ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>"Gitxen 1.2.3", from the assembly, so the bar never disagrees with the build.</summary>
    public string AppVersionText { get; } = BuildAppVersionText();

    private static string BuildAppVersionText()
    {
        Version? version = typeof(StatusBar).Assembly.GetName().Version;
        return version is null ? "Gitxen" : $"Gitxen {version.ToString(3)}";
    }

    /// <summary>Guards the git-version read, which is per process, not per load.</summary>
    private bool _gitVersionRead;

    /// <summary>
    ///  Fills in the git version once the bar is up.
    /// </summary>
    /// <remarks>
    ///  <para>
    ///   Asks git directly, through Gitxen's own <see cref="GitCli"/> rather than the inherited
    ///   engine. This is the first thing wired to that layer: a single command whose output is one
    ///   line, so it exercises the process launching, the redirection and the decoding without
    ///   depending on any parser yet.
    ///  </para>
    ///  <para>
    ///   Awaited rather than blocking: launching a process costs most of a tenth of a second, and
    ///   the bar simply shows nothing there until the answer arrives.
    ///  </para>
    /// </remarks>
    private async void StatusBar_Loaded(object sender, RoutedEventArgs e)
    {
        ShowSide();

        if (_gitVersionRead)
        {
            return;
        }

        _gitVersionRead = true;
        _ = ShowGhVersionAsync();

        try
        {
            // No repository is involved in asking git its version, so this runs wherever the
            // application happens to be.
            GitCli git = new(AppContext.BaseDirectory);
            GitResult result = await git.ReadAsync(new GitArgs("--version"));

            // "git version 2.52.0.windows.1" -> "git 2.52.0.windows.1"
            GitVersionText.Text = result.Succeeded
                ? result.Output.Replace("git version ", "git ", StringComparison.Ordinal)
                : "";
        }
        catch (Exception)
        {
            // No git on PATH at all. That is Home-page news the InfoBars already break; the status
            // bar stays quiet rather than repeating it.
            GitVersionText.Text = "";
        }
    }

    /// <summary>The GitHub CLI's version and active account, from the status gh is already asked for.</summary>
    private async Task ShowGhVersionAsync()
    {
        try
        {
            Gitxen.GitHub.GhStatus status = await Gitxen.Services.GitHubService.GetStatusAsync();

            GhVersionText.Text = status.Version.Length == 0
                ? ""
                : status.Login.Length > 0 ? $"gh {status.Version} · {status.Login}" : $"gh {status.Version}";
        }
        catch (Exception)
        {
            // No gh: nothing to say, and the GitHub pages explain it where it matters.
            GhVersionText.Text = "";
        }
    }

    /// <summary>Raised with a navigation tag when a segment asks for its section.</summary>
    public event EventHandler<string>? SectionRequested;

    /// <summary>Raised when the console segment asks for the git activity pane.</summary>
    public event EventHandler? ConsoleRequested;

    /// <summary>Raised with the new scale, as a fraction, when the zoom slider is moved.</summary>
    public event EventHandler<double>? ZoomRequested;

    /// <summary>
    ///  Puts the stored zoom on the slider without treating it as the user moving it.
    /// </summary>
    /// <remarks>
    ///  Assigning Value raises ValueChanged, which would ask the window to apply a zoom it has just
    ///  told us about. Harmless but circular, and it would fire before the window is ready.
    /// </remarks>
    public void ShowZoom(double scale)
    {
        _syncingZoom = true;
        ZoomSlider.Value = Math.Round(scale * 100);
        _syncingZoom = false;

        UpdateZoomText();
    }

    private bool _syncingZoom;

    private void Zoom_ValueChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        UpdateZoomText();

        if (!_syncingZoom)
        {
            ZoomRequested?.Invoke(this, e.NewValue / 100);
        }
    }

    /// <summary>The reset button is the percentage itself: what it reads is what clicking it undoes.</summary>
    private void ZoomReset_Click(object sender, RoutedEventArgs e)
    {
        ZoomSlider.Value = 100;
    }

    private void UpdateZoomText()
    {
        if (ZoomText is not null)
        {
            ZoomText.Text = $"{ZoomSlider.Value:0}%";
        }
    }

    private void Console_Click(object sender, RoutedEventArgs e) =>
        ConsoleRequested?.Invoke(this, EventArgs.Empty);

    private void Branch_Click(object sender, RoutedEventArgs e) => SectionRequested?.Invoke(this, "branches");

    private void Changes_Click(object sender, RoutedEventArgs e) => SectionRequested?.Invoke(this, "changes");

    private void Conflicts_Click(object sender, RoutedEventArgs e) => SectionRequested?.Invoke(this, "conflicts");

    /// <summary>Conflicts is where a paused operation is resolved, so that is where its segment goes.</summary>
    private void Operation_Click(object sender, RoutedEventArgs e) => SectionRequested?.Invoke(this, "conflicts");

    private async void Sync_Click(object sender, RoutedEventArgs e)
    {
        if (Tab is RepositoryTabViewModel tab)
        {
            tab.Report(await tab.FetchAsync());
        }
    }
}
