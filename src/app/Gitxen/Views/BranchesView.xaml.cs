// SPDX-License-Identifier: GPL-3.0-or-later
using Gitxen.Models;
using Gitxen.Services;
using Gitxen.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.ApplicationModel.DataTransfer;

namespace Gitxen.Views;

/// <summary>
///  Local branches with their tracking state, and everything you can do to one.
/// </summary>
/// <remarks>
///  Replaces a menu that could create, delete or merge a branch only by typing its name into a
///  prompt — with no list to check the name against. Here the branch is the thing you click, and its
///  ahead/behind counts are on screen while you decide.
/// </remarks>
public sealed partial class BranchesView : RepositoryPage
{
    public BranchesView()
    {
        InitializeComponent();
    }

    public override async Task ActivateAsync()
    {
        if (Tab is RepositoryTabViewModel tab)
        {
            await tab.LoadBranchesAsync();
            await tab.LoadRemoteBranchesAsync();
        }
    }

    private static RemoteBranchInfo? RemoteFrom(object sender) =>
        (sender as FrameworkElement)?.DataContext as RemoteBranchInfo;

    /// <summary>
    ///  Checking out a remote branch means creating a local branch that tracks it — you cannot commit
    ///  onto a remote-tracking ref.
    /// </summary>
    private async void CheckoutRemote_Click(object sender, RoutedEventArgs e)
    {
        if (RemoteFrom(sender) is not RemoteBranchInfo remote)
        {
            return;
        }

        if (await PromptAsync("Check out remote branch", "Local branch name", "Check out", remote.SuggestedLocalName)
            is string name && name.Length > 0)
        {
            await ReportAsync(tab => tab.CreateBranchAtAsync(name, remote.FullName, checkout: true));
            await ActivateAsync();
        }
    }

    private async void MergeRemote_Click(object sender, RoutedEventArgs e)
    {
        if (RemoteFrom(sender) is not RemoteBranchInfo remote || Tab is not RepositoryTabViewModel tab)
        {
            return;
        }

        if (await ConfirmAsync(
            "Merge remote branch?",
            $"Merge '{remote.FullName}' into '{tab.Branch}'?",
            "Merge"))
        {
            await ReportAsync(t => t.MergeBranchAsync(remote.FullName));
            await ActivateAsync();
        }
    }

    private async void DeleteRemote_Click(object sender, RoutedEventArgs e)
    {
        if (RemoteFrom(sender) is not RemoteBranchInfo remote)
        {
            return;
        }

        // This one reaches off the machine and there is no remote reflog to undo it from.
        if (await ConfirmAsync(
            "Delete branch on the remote?",
            $"Deletes '{remote.ShortName}' from '{remote.Remote}'. This affects everyone using that remote "
                + "and cannot be undone from here.",
            "Delete on remote"))
        {
            await ReportAsync(tab => tab.DeleteRemoteBranchAsync(remote.Remote, remote.ShortName));
            await ActivateAsync();
        }
    }

    private void CopyRemoteName_Click(object sender, RoutedEventArgs e)
    {
        if (RemoteFrom(sender) is not RemoteBranchInfo remote || Tab is not RepositoryTabViewModel tab)
        {
            return;
        }

        DataPackage package = new();
        package.SetText(remote.FullName);
        Clipboard.SetContent(package);

        tab.ReportInformation("Copied", $"{remote.FullName} copied to the clipboard.");
    }

    /// <summary>The branch the context menu or a double-click is acting on.</summary>
    private static BranchInfo? BranchFrom(object sender) =>
        (sender as FrameworkElement)?.DataContext as BranchInfo;

    private async void Refresh_Click(object sender, RoutedEventArgs e) => await RefreshAsync();

    /// <summary>
    ///  Lists branches that look finished with, ticks the safe ones (merged, or their remote deleted),
    ///  and deletes what is ticked. One Undo on the result bar brings every one of them back.
    /// </summary>
    private async void Cleanup_Click(object sender, RoutedEventArgs e)
    {
        if (Tab is not RepositoryTabViewModel tab)
        {
            return;
        }

        IReadOnlyList<CleanupCandidate> candidates = await tab.FindCleanupCandidatesAsync();

        if (candidates.Count == 0)
        {
            tab.ReportInformation("Nothing to clean up", "No branch here is merged, has lost its remote branch, or has been idle for "
                + $"{RepositoryLoader.StaleDays} days. The current and default branches are never offered.");
            return;
        }

        List<(CheckBox Box, CleanupCandidate Candidate)> rows = [];
        StackPanel list = new() { Spacing = 6 };

        foreach (CleanupCandidate candidate in candidates)
        {
            CheckBox box = new() { IsChecked = candidate.IsSafe };
            StackPanel label = new() { Spacing = 1 };
            label.Children.Add(new TextBlock { Text = candidate.Name, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis });
            label.Children.Add(new TextBlock
            {
                Text = $"{string.Join(", ", candidate.Reasons.Select(reason => BranchCleanup.Describe(reason, RepositoryLoader.StaleDays)))} · last commit {TimeText.Relative(candidate.LastCommit)}",
                FontSize = 12,
                Opacity = 0.7,
                TextTrimming = TextTrimming.CharacterEllipsis
            });
            box.Content = label;
            rows.Add((box, candidate));
            list.Children.Add(box);
        }

        TextBlock note = new()
        {
            Text = "Merged branches and those whose remote branch is gone are ticked. Idle branches, and branches matched to a merged pull request by name, are listed for you to decide. Only local branches are deleted, and Undo brings them all back.",
            TextWrapping = TextWrapping.Wrap,
            Opacity = 0.7
        };

        StackPanel content = new() { Spacing = 12, Width = 500 };
        content.Children.Add(note);
        content.Children.Add(new ScrollViewer { Content = list, MaxHeight = 380 });

        ContentDialog dialog = new()
        {
            Title = $"Clean up branches ({candidates.Count})",
            Content = content,
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = XamlRoot
        };

        void Refresh()
        {
            int ticked = rows.Count(row => row.Box.IsChecked == true);
            dialog.PrimaryButtonText = ticked == 1 ? "Delete 1 branch" : $"Delete {ticked} branches";
            dialog.IsPrimaryButtonEnabled = ticked > 0;
        }

        foreach ((CheckBox box, _) in rows)
        {
            box.Checked += (_, _) => Refresh();
            box.Unchecked += (_, _) => Refresh();
        }

        Refresh();

        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
        {
            return;
        }

        IReadOnlyList<string> chosen = [.. rows.Where(row => row.Box.IsChecked == true).Select(row => row.Candidate.Name)];
        tab.Report(await tab.DeleteBranchesAsync(chosen));
    }

    private async void NewBranch_Click(object sender, RoutedEventArgs e)
    {
        if (await PromptAsync("New branch", "Branch name", "Create") is string name && name.Length > 0)
        {
            await ReportAsync(tab => tab.CreateBranchAsync(name, checkout: true));
            await ActivateAsync();
        }
    }

    private async void BranchList_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        if (BranchList.SelectedItem is BranchInfo branch && !branch.IsCurrent)
        {
            await CheckoutAsync(branch);
        }
    }

    private async void Checkout_Click(object sender, RoutedEventArgs e)
    {
        if (BranchFrom(sender) is BranchInfo branch)
        {
            await CheckoutAsync(branch);
        }
    }

    private async void Merge_Click(object sender, RoutedEventArgs e)
    {
        if (BranchFrom(sender) is not BranchInfo branch || Tab is not RepositoryTabViewModel tab)
        {
            return;
        }

        if (await ConfirmAsync(
            "Merge branch?",
            $"Merge '{branch.Name}' into '{tab.Branch}'? Conflicts will stop the merge part-way.",
            "Merge"))
        {
            await ReportAsync(t => t.MergeBranchAsync(branch.Name));
            await ActivateAsync();
        }
    }

    private async void Rename_Click(object sender, RoutedEventArgs e)
    {
        if (BranchFrom(sender) is not BranchInfo branch)
        {
            return;
        }

        if (await PromptAsync("Rename branch", "New name", "Rename", branch.Name) is string name
            && name.Length > 0
            && name != branch.Name)
        {
            await ReportAsync(tab => tab.RenameBranchAsync(branch.Name, name));
            await ActivateAsync();
        }
    }

    private async void SetUpstream_Click(object sender, RoutedEventArgs e)
    {
        if (BranchFrom(sender) is not BranchInfo branch)
        {
            return;
        }

        string suggestion = branch.HasUpstream ? branch.Upstream : $"origin/{branch.Name}";

        if (await PromptAsync("Set upstream", "Remote-tracking branch", "Set", suggestion) is string upstream
            && upstream.Length > 0)
        {
            await ReportAsync(tab => tab.SetUpstreamAsync(branch.Name, upstream));
            await ActivateAsync();
        }
    }

    private async void Delete_Click(object sender, RoutedEventArgs e)
    {
        if (BranchFrom(sender) is not BranchInfo branch)
        {
            return;
        }

        if (branch.IsCurrent)
        {
            Tab?.ReportInformation("Cannot delete", "That branch is checked out. Switch to another branch first.");
            return;
        }

        // -d, not -D: git refuses to drop unmerged work, which is the safer default. Anything genuinely
        // unmerged is reported back rather than silently discarded.
        if (await ConfirmAsync("Delete branch?", $"Delete '{branch.Name}'?", "Delete"))
        {
            await ReportAsync(tab => tab.DeleteBranchAsync(branch.Name, force: false));
            await ActivateAsync();
        }
    }

    private void CopyName_Click(object sender, RoutedEventArgs e)
    {
        if (BranchFrom(sender) is not BranchInfo branch || Tab is not RepositoryTabViewModel tab)
        {
            return;
        }

        DataPackage package = new();
        package.SetText(branch.Name);
        Clipboard.SetContent(package);

        tab.ReportInformation("Copied", $"{branch.Name} copied to the clipboard.");
    }

    private async Task CheckoutAsync(BranchInfo branch)
    {
        await ReportAsync(tab => tab.CheckoutBranchAsync(branch.Name));
        await ActivateAsync();
    }
}
