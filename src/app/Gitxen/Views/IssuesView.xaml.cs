// SPDX-License-Identifier: GPL-3.0-or-later
using Gitxen.Services;
using Gitxen.ViewModels;
using Microsoft.UI.Xaml;
using Windows.ApplicationModel.DataTransfer;

namespace Gitxen.Views;

/// <summary>Issues for a repository on GitHub: the list, the selected one in full, and what you do to it.</summary>
public sealed partial class IssuesView : RepositoryPage
{
    public IssuesView()
    {
        InitializeComponent();
    }

    private GitHubIssuesViewModel? Model => Tab?.GitHub.Issues;

    private IssueRow? Current => Model?.Selected;

    public override async Task ActivateAsync()
    {
        if (Model is GitHubIssuesViewModel model)
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
            await Windows.System.Launcher.LaunchUriAsync(new Uri(host.IssuesUrl));
        }
    }

    private async void NewIssue_Click(object sender, RoutedEventArgs e)
    {
        if (Model is not GitHubIssuesViewModel model)
        {
            return;
        }

        if (await GitHubDialogs.NewIssueAsync(XamlRoot) is (string title, string body))
        {
            await model.CreateAsync(title, body);
        }
    }

    private async void Comment_Click(object sender, RoutedEventArgs e)
    {
        if (Model is not GitHubIssuesViewModel model || Current is not IssueRow row)
        {
            return;
        }

        if (await GitHubDialogs.CommentAsync(XamlRoot, $"Comment on #{row.Number}") is string body)
        {
            await model.CommentAsync(row, body);
        }
    }

    private async void Close_Click(object sender, RoutedEventArgs e)
    {
        if (Model is not GitHubIssuesViewModel model || Current is not IssueRow row)
        {
            return;
        }

        if (await GitHubDialogs.CloseIssueAsync(XamlRoot, row) is string reason)
        {
            await model.CloseAsync(row, reason);
        }
    }

    private async void Reopen_Click(object sender, RoutedEventArgs e)
    {
        if (Model is GitHubIssuesViewModel model && Current is IssueRow row)
        {
            await model.ReopenAsync(row);
        }
    }

    private async void OpenOnGitHub_Click(object sender, RoutedEventArgs e)
    {
        if (Current is IssueRow row && Uri.TryCreate(row.Model.Url, UriKind.Absolute, out Uri? url))
        {
            await Windows.System.Launcher.LaunchUriAsync(url);
        }
    }

    private void CopyLink_Click(object sender, RoutedEventArgs e)
    {
        if (Current is not IssueRow row)
        {
            return;
        }

        DataPackage package = new();
        package.SetText(row.Model.Url);
        Clipboard.SetContent(package);

        Tab?.ReportInformation("Copied", $"{row.Model.Url} copied to the clipboard.");
    }
}
