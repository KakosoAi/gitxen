// SPDX-License-Identifier: GPL-3.0-or-later
using Gitxen.Services;
using Gitxen.ViewModels;
using Microsoft.UI.Xaml;
using Windows.Storage;
using Windows.Storage.Pickers;
using WinRT.Interop;

namespace Gitxen.Views;

/// <summary>
///  Repository housekeeping: removing untracked files, compacting the object store, exporting a
///  snapshot.
/// </summary>
/// <remarks>
///  These are grouped away from the everyday pages on purpose. Cleaning deletes files that git has
///  never recorded, so unlike almost everything else in this app it cannot be undone from the
///  repository itself — which is why the preview is offered first and reads out the actual list.
/// </remarks>
public sealed partial class MaintenanceView : RepositoryPage
{
    private bool _showingSpeed;

    public MaintenanceView()
    {
        InitializeComponent();
    }

    public override async Task ActivateAsync()
    {
        if (Tab is RepositoryTabViewModel tab)
        {
            ShowSpeed(await tab.RefreshSpeedAsync());
        }
    }

    private void ShowSpeed(SpeedSettings speed)
    {
        static string Mark(bool on) => on ? "on " : "off";

        SpeedState.Text =
            $"commit-graph      {Mark(speed.CommitGraph)}\n" +
            $"kept up by fetch  {Mark(speed.FetchWritesGraph)}\n" +
            $"untracked cache   {Mark(speed.UntrackedCache)}\n" +
            $"file watcher      {Mark(speed.FsMonitor)}{(speed.FsMonitorPossible ? "" : "  (not on a network drive)")}\n" +
            $"size              {(speed.Commits >= SpeedSettings.LargeCommits ? $"{speed.Commits:N0}+" : $"{speed.Commits:N0}")} commits, {speed.TrackedFiles:N0} files";

        SpeedUpButton.IsEnabled = !(speed.CommitGraph && speed.FetchWritesGraph && speed.UntrackedCache);

        _showingSpeed = true;
        WatcherSwitch.IsOn = speed.FsMonitor;
        WatcherSwitch.IsEnabled = speed.FsMonitorPossible;
        _showingSpeed = false;
    }

    private async void SpeedUp_Click(object sender, RoutedEventArgs e)
    {
        if (Tab is RepositoryTabViewModel tab)
        {
            await tab.ApplySpeedUpsAsync(WatcherSwitch.IsOn);

            if (tab.Speed is SpeedSettings speed)
            {
                ShowSpeed(speed);
            }
        }
    }

    private async void WatcherSwitch_Toggled(object sender, RoutedEventArgs e)
    {
        if (_showingSpeed || Tab is not RepositoryTabViewModel tab)
        {
            return;
        }

        if (WatcherSwitch.IsOn)
        {
            await tab.ApplySpeedUpsAsync(fileWatcher: true);
        }
        else
        {
            await tab.StopFileWatcherAsync();
        }

        if (tab.Speed is SpeedSettings speed)
        {
            ShowSpeed(speed);
        }
    }

    private async void CleanPreview_Click(object sender, RoutedEventArgs e)
    {
        if (Tab is not RepositoryTabViewModel tab)
        {
            return;
        }

        GitOperationResult result = await tab.CleanAsync(
            CleanDirectories.IsChecked == true,
            CleanIgnored.IsChecked == true,
            dryRun: true);

        await ShowTextAsync(
            "Would be removed",
            string.IsNullOrWhiteSpace(result.Output) ? "Nothing would be removed." : result.Output);
    }

    private async void Clean_Click(object sender, RoutedEventArgs e)
    {
        bool directories = CleanDirectories.IsChecked == true;
        bool ignored = CleanIgnored.IsChecked == true;

        string warning = ignored
            ? "This deletes untracked files including ignored ones — build output, local configuration "
                + "and anything else git does not track. It cannot be undone."
            : "This deletes untracked files. They are not in git, so there is nothing to restore them from.";

        if (await ConfirmAsync("Remove untracked files?", warning, "Remove"))
        {
            await ReportAsync(tab => tab.CleanAsync(directories, ignored, dryRun: false));
        }
    }

    private async void CollectGarbage_Click(object sender, RoutedEventArgs e) =>
        await ReportAsync(tab => tab.CollectGarbageAsync());

    private async void Archive_Click(object sender, RoutedEventArgs e)
    {
        if (Tab is not RepositoryTabViewModel tab)
        {
            return;
        }

        string reference = ArchiveReference.Text.Trim();

        if (reference.Length == 0)
        {
            tab.ReportInformation("Nothing to export", "Give a revision to archive, such as HEAD or a tag.");
            return;
        }

        FileSavePicker picker = new() { SuggestedFileName = $"{tab.Title}-{reference}" };
        picker.FileTypeChoices.Add("Zip archive", [".zip"]);
        picker.FileTypeChoices.Add("Tar archive", [".tar"]);

        if (App.Shell is not Window shell)
        {
            return;
        }

        // A picker needs an owning window; in a desktop app that has to be supplied explicitly.
        InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(shell));

        StorageFile? file = await picker.PickSaveFileAsync();

        if (file is not null)
        {
            await ReportAsync(t => t.ArchiveAsync(reference, file.Path));
        }
    }
}
