// SPDX-License-Identifier: GPL-3.0-or-later
using Gitxen.GitHub;
using Gitxen.Services;
using Gitxen.ViewModels;
using Microsoft.UI.Xaml;
using Windows.ApplicationModel.DataTransfer;

namespace Gitxen.Views;

/// <summary>
///  Workflow runs for a repository on GitHub: the list, the selected run's jobs and steps, its log,
///  and re-running or cancelling it.
/// </summary>
/// <remarks>
///  A run that is still going refreshes itself every ten seconds while this page is showing, so a
///  build can be watched from here rather than by pressing Refresh.
/// </remarks>
public sealed partial class ActionsView : RepositoryPage
{
    public ActionsView()
    {
        InitializeComponent();
    }

    private GitHubActionsViewModel? Model => Tab?.GitHub.Actions;

    private RunRow? Current => Model?.Selected;

    protected override void OnTabChanged()
    {
        // Polling is pointless for a page nobody is looking at, so the view model asks whether this
        // one is the page on screen before it spends a request.
        if (Model is GitHubActionsViewModel model)
        {
            model.IsPageVisible = () => Visibility == Visibility.Visible && IsLoaded;
        }
    }

    public override async Task ActivateAsync()
    {
        if (Model is GitHubActionsViewModel model)
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
            await Windows.System.Launcher.LaunchUriAsync(new Uri($"{host.BrowseUrl}/actions"));
        }
    }

    private async void RunWorkflow_Click(object sender, RoutedEventArgs e)
    {
        if (Model is not GitHubActionsViewModel model || Tab is not RepositoryTabViewModel tab)
        {
            return;
        }

        IReadOnlyList<Workflow> workflows = await model.GetWorkflowsAsync();

        if (workflows.Count == 0)
        {
            tab.ReportInformation("No workflows", "This repository has no workflows to run.");
            return;
        }

        if (await GitHubDialogs.RunWorkflowAsync(XamlRoot, workflows, tab.Branch) is (Workflow workflow, string branch))
        {
            await model.RunWorkflowAsync(workflow.Name, branch);
        }
    }

    private async void RerunAll_Click(object sender, RoutedEventArgs e)
    {
        if (Model is GitHubActionsViewModel model && Current is RunRow row)
        {
            await model.RerunAsync(row, failedOnly: false);
        }
    }

    private async void RerunFailed_Click(object sender, RoutedEventArgs e)
    {
        if (Model is GitHubActionsViewModel model && Current is RunRow row)
        {
            await model.RerunAsync(row, failedOnly: true);
        }
    }

    private async void Cancel_Click(object sender, RoutedEventArgs e)
    {
        if (Model is not GitHubActionsViewModel model || Current is not RunRow row)
        {
            return;
        }

        if (await ConfirmAsync("Cancel this run?", $"\"{row.Title}\" will be stopped where it is.", "Cancel run"))
        {
            await model.CancelAsync(row);
        }
    }

    private async void FailedLog_Click(object sender, RoutedEventArgs e) => await ShowLogAsync(failedOnly: true);

    private async void FullLog_Click(object sender, RoutedEventArgs e) => await ShowLogAsync(failedOnly: false);

    private async Task ShowLogAsync(bool failedOnly)
    {
        if (Model is not GitHubActionsViewModel model || Current is not RunRow row)
        {
            return;
        }

        (bool ok, string text) = await model.GetLogAsync(row, failedOnly);

        if (!ok)
        {
            Tab?.Report(new GitOperationResult("Read the log", false, text));
            return;
        }

        await ShowTextAsync(
            $"{row.Title} — {(failedOnly ? "failed steps" : "full log")}",
            string.IsNullOrWhiteSpace(text) ? "The log is empty." : text);
    }

    private async void OpenOnGitHub_Click(object sender, RoutedEventArgs e)
    {
        if (Current is RunRow row && Uri.TryCreate(row.Model.Url, UriKind.Absolute, out Uri? url))
        {
            await Windows.System.Launcher.LaunchUriAsync(url);
        }
    }

    private void CopyLink_Click(object sender, RoutedEventArgs e)
    {
        if (Current is not RunRow row)
        {
            return;
        }

        DataPackage package = new();
        package.SetText(row.Model.Url);
        Clipboard.SetContent(package);

        Tab?.ReportInformation("Copied", $"{row.Model.Url} copied to the clipboard.");
    }
}
