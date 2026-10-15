// SPDX-License-Identifier: GPL-3.0-or-later
using Gitxen.GitHub;
using Gitxen.Services;
using Gitxen.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.ApplicationModel.DataTransfer;

namespace Gitxen.Views;

/// <summary>
///  Pull requests for a repository on GitHub: the list, the selected one in full, and the things you
///  do to it.
/// </summary>
/// <remarks>
///  Everything is read and done through the GitHub CLI, so this page holds no credential and has no
///  sign-in of its own. When gh cannot be used the page shows why in place of itself.
/// </remarks>
public sealed partial class PullRequestsView : RepositoryPage
{
    public PullRequestsView()
    {
        InitializeComponent();
    }

    private GitHubPullRequestsViewModel? Model => Tab?.GitHub.PullRequests;

    public override async Task ActivateAsync()
    {
        if (Model is GitHubPullRequestsViewModel model)
        {
            await model.LoadAsync();
        }
    }

    private async void Refresh_Click(object sender, RoutedEventArgs e) => await RefreshAsync();

    private async void Gate_Rechecked(object? sender, EventArgs e) => await ActivateAsync();

    private async void Account_Changed(object? sender, EventArgs e) => await ActivateAsync();

    private async void OpenList_Click(object sender, RoutedEventArgs e)
    {
        if (Tab?.Host is HostedRepository host)
        {
            await Windows.System.Launcher.LaunchUriAsync(new Uri(host.PullRequestsUrl));
        }
    }

    // ---- The selected pull request ----

    private PullRequestRow? Current => Model?.Selected;

    private async void Checkout_Click(object sender, RoutedEventArgs e)
    {
        if (Model is GitHubPullRequestsViewModel model && Current is PullRequestRow row)
        {
            await model.CheckoutAsync(row);
        }
    }

    private async void Merge_Click(object sender, RoutedEventArgs e)
    {
        if (Model is not GitHubPullRequestsViewModel model || Current is not PullRequestRow row)
        {
            return;
        }

        string? tag = (sender as FrameworkElement)?.Tag as string;

        MergeMethod method = tag switch
        {
            "1" => MergeMethod.Squash,
            "2" => MergeMethod.Rebase,
            _ => MergeMethod.Merge
        };

        if (await GitHubDialogs.MergeAsync(XamlRoot, row, method) is (MergeMethod chosen, bool deleteBranch))
        {
            await model.MergeAsync(row, chosen, deleteBranch);
        }
    }

    private async void Review_Click(object sender, RoutedEventArgs e)
    {
        if (Model is not GitHubPullRequestsViewModel model || Current is not PullRequestRow row)
        {
            return;
        }

        if (await GitHubDialogs.ReviewAsync(XamlRoot, row.Number) is (ReviewAction action, string body))
        {
            await model.ReviewAsync(row, action, body);
        }
    }

    private async void Comment_Click(object sender, RoutedEventArgs e)
    {
        if (Model is not GitHubPullRequestsViewModel model || Current is not PullRequestRow row)
        {
            return;
        }

        if (await GitHubDialogs.CommentAsync(XamlRoot, $"Comment on #{row.Number}") is string body)
        {
            await model.CommentAsync(row, body);
        }
    }

    private async void Ready_Click(object sender, RoutedEventArgs e)
    {
        if (Model is GitHubPullRequestsViewModel model && Current is PullRequestRow row)
        {
            await model.MarkReadyAsync(row);
        }
    }

    private async void Close_Click(object sender, RoutedEventArgs e)
    {
        if (Model is not GitHubPullRequestsViewModel model || Current is not PullRequestRow row)
        {
            return;
        }

        if (await ConfirmAsync(
            $"Close #{row.Number}?",
            $"\"{row.Title}\" will be closed without merging. It can be reopened later.",
            "Close pull request"))
        {
            await model.CloseAsync(row);
        }
    }

    private async void Reopen_Click(object sender, RoutedEventArgs e)
    {
        if (Model is GitHubPullRequestsViewModel model && Current is PullRequestRow row)
        {
            await model.ReopenAsync(row);
        }
    }

    private async void OpenOnGitHub_Click(object sender, RoutedEventArgs e)
    {
        if (Current is PullRequestRow row && Uri.TryCreate(row.Model.Url, UriKind.Absolute, out Uri? url))
        {
            await Windows.System.Launcher.LaunchUriAsync(url);
        }
    }

    private void CopyLink_Click(object sender, RoutedEventArgs e)
    {
        if (Current is not PullRequestRow row)
        {
            return;
        }

        DataPackage package = new();
        package.SetText(row.Model.Url);
        Clipboard.SetContent(package);

        Tab?.ReportInformation("Copied", $"{row.Model.Url} copied to the clipboard.");
    }

    // ---- Creating ----

    private async void NewPullRequest_Click(object sender, RoutedEventArgs e)
    {
        if (Tab is not RepositoryTabViewModel tab)
        {
            return;
        }

        NewPullRequestRequest? request = await GitHubDialogs.NewPullRequestAsync(XamlRoot, tab.Branch, tab.GitHub.DefaultBranch, tab.WritePullRequestAsync);

        if (request is not null)
        {
            await tab.GitHub.PullRequests.CreateAsync(request.Title, request.Body, request.BaseBranch, request.Head, request.Draft);
        }
    }

    // ---- The detail pane's sections ----

    private void Sections_SelectionChanged(SelectorBar sender, SelectorBarSelectionChangedEventArgs args)
    {
        ConversationPane.Visibility = ReferenceEquals(sender.SelectedItem, ConversationItem) ? Visibility.Visible : Visibility.Collapsed;
        FilesPane.Visibility = ReferenceEquals(sender.SelectedItem, FilesItem) ? Visibility.Visible : Visibility.Collapsed;
        ChecksPane.Visibility = ReferenceEquals(sender.SelectedItem, ChecksItem) ? Visibility.Visible : Visibility.Collapsed;

        // Something in the diff straight away rather than an empty panel awaiting a click.
        if (ReferenceEquals(sender.SelectedItem, FilesItem) && FileList.SelectedItem is null && FileList.Items.Count > 0)
        {
            FileList.SelectedIndex = 0;
        }
    }

    private async void File_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (Model is GitHubPullRequestsViewModel model)
        {
            await model.ShowFileDiffAsync(FileList.SelectedItem as FileRow);
        }
    }
}
