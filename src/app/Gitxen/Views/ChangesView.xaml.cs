// SPDX-License-Identifier: GPL-3.0-or-later
using Gitxen.Diff;
using Gitxen.Services;
using Gitxen.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.ApplicationModel.DataTransfer;

namespace Gitxen.Views;

/// <summary>
///  Staging and committing: the uncommitted work split into staged and unstaged, the diff of the
///  selected file, and the message being written.
/// </summary>
/// <remarks>
///  A page rather than the modal dialog this replaces. Reviewing a change set means moving between
///  files and back and forth across the staged boundary, which a fixed-size dialog cannot hold — and
///  a half-written commit message now survives navigating away.
/// </remarks>
public sealed partial class ChangesView : RepositoryPage
{
    public ChangesView()
    {
        InitializeComponent();

        // The pane opens at whatever width it was last dragged to, on every tab. SizeChanged is the
        // write-back: the pane's width only ever changes when the splitter drags it, so anything the
        // event reports is a deliberate choice worth keeping.
        StagingPane.Width = AppOptions.StagingPaneWidth;
        StagingPane.SizeChanged += (_, e) => AppOptions.StagingPaneWidth = e.NewSize.Width;
    }

    /// <summary>The diff currently subscribed to, so the handler is not attached twice.</summary>
    private DiffViewModel? _subscribedDiff;

    public override async Task ActivateAsync()
    {
        if (Tab is RepositoryTabViewModel tab)
        {
            SubscribeToHunkActions(tab.Changes.Diff);
            await tab.Changes.RefreshAsync();
        }
    }

    protected override void OnTabChanged()
    {
        if (Tab is RepositoryTabViewModel tab)
        {
            SubscribeToHunkActions(tab.Changes.Diff);
        }
    }

    private void SubscribeToHunkActions(DiffViewModel diff)
    {
        if (ReferenceEquals(_subscribedDiff, diff))
        {
            return;
        }

        if (_subscribedDiff is not null)
        {
            _subscribedDiff.HunkActionRequested -= Diff_HunkActionRequested;
        }

        diff.HunkActionRequested += Diff_HunkActionRequested;
        _subscribedDiff = diff;
    }

    /// <summary>
    ///  Stages or unstages one hunk, leaving the rest of the file where it was.
    /// </summary>
    /// <remarks>
    ///  A failure here is worth showing in full: <c>git apply</c> refusing a hunk usually means the
    ///  file moved on underneath the diff, and the message says which line stopped matching.
    /// </remarks>
    private async void Diff_HunkActionRequested(object? sender, DiffHunk hunk)
    {
        if (Tab is not RepositoryTabViewModel tab)
        {
            return;
        }

        GitOperationResult result = await tab.Changes.ApplyHunkAsync(hunk);

        if (!result.Succeeded)
        {
            tab.Report(result);
        }
    }

    /// <summary>
    ///  One button per row for both directions: which way it moves the file follows from which list the
    ///  file is currently in, so there is never a disabled Stage next to an enabled Unstage.
    /// </summary>
    private async void ToggleStage_Click(object sender, RoutedEventArgs e)
    {
        if (Tab is not RepositoryTabViewModel tab || sender is not Button { Tag: ChangedFileViewModel file })
        {
            return;
        }

        GitOperationResult result = file.IsStaged
            ? await tab.Changes.UnstageAsync(file)
            : await tab.Changes.StageAsync(file);

        if (!result.Succeeded)
        {
            tab.Report(result);
        }
    }

    private async void StageAll_Click(object sender, RoutedEventArgs e)
    {
        if (Tab is RepositoryTabViewModel tab)
        {
            tab.Report(await tab.Changes.StageAllAsync());
        }
    }

    private async void UnstageAll_Click(object sender, RoutedEventArgs e)
    {
        if (Tab is RepositoryTabViewModel tab)
        {
            tab.Report(await tab.Changes.UnstageAllAsync());
        }
    }

    private async void Amend_Checked(object sender, RoutedEventArgs e)
    {
        // Ticking Amend offers HEAD's message, so rewording is one edit rather than a retype.
        if (Tab is RepositoryTabViewModel tab)
        {
            await tab.Changes.PrepareAmendAsync();
        }
    }

    /// <summary>Ctrl+Enter commits from the message box, the shortcut every git client has.</summary>
    private async void CommitAccelerator_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        args.Handled = true;

        if (Tab is RepositoryTabViewModel tab && tab.Changes.CanCommit)
        {
            await CommitGuardedAsync(tab);
        }
    }

    /// <summary>Fills the message box from the staged changes, written by Claude. Nothing is sent until pressed.</summary>
    private async void WriteMessage_Click(object sender, RoutedEventArgs e)
    {
        if (Tab is not RepositoryTabViewModel tab)
        {
            return;
        }

        WriteMessageButton.IsEnabled = false;
        WriteMessageLabel.Text = "Writing…";

        try
        {
            tab.Changes.Message = await tab.WriteCommitMessageAsync();
        }
        catch (AiException ex)
        {
            tab.ReportInformation("AI writing", ex.Message);
        }
        finally
        {
            WriteMessageButton.IsEnabled = true;
            WriteMessageLabel.Text = "Write with AI";
        }
    }

    private async void Commit_Click(object sender, RoutedEventArgs e)
    {
        if (Tab is RepositoryTabViewModel tab)
        {
            await CommitGuardedAsync(tab);
        }
    }

    /// <summary>
    ///  Commits, unless the staged changes carry a secret, a file that usually holds one, or a file too
    ///  big for a repository — then it asks first, and can take those files out of the commit instead.
    /// </summary>
    private async Task CommitGuardedAsync(RepositoryTabViewModel tab)
    {
        IReadOnlyList<ScanFinding> findings = await tab.ScanStagedAsync();

        if (findings.Count > 0)
        {
            switch (await SecretGuardDialog.ShowAsync(XamlRoot, findings, forPush: false))
            {
                case GuardChoice.Cancel:
                    return;

                case GuardChoice.Unstage:
                    tab.Report(await tab.UnstageFlaggedAsync(findings));
                    return;
            }
        }

        tab.Report(await tab.Changes.CommitAsync());
    }

    private async void Discard_Click(object sender, RoutedEventArgs e)
    {
        if (Tab is not RepositoryTabViewModel tab
            || sender is not FrameworkElement { DataContext: ChangedFileViewModel file })
        {
            return;
        }

        // Untracked files are deleted rather than reverted, and there is no reflog to recover either.
        string message = file.IsNew
            ? $"{file.Name} is not tracked by git. Discarding it deletes the file, and it cannot be recovered."
            : $"This throws away every uncommitted change to {file.Name}. It cannot be undone.";

        if (await ConfirmAsync("Discard changes?", message, file.IsNew ? "Delete file" : "Discard"))
        {
            tab.Report(await tab.Changes.DiscardAsync(file));
        }
    }

    private void CopyPath_Click(object sender, RoutedEventArgs e)
    {
        if (Tab is not RepositoryTabViewModel tab
            || sender is not FrameworkElement { DataContext: ChangedFileViewModel file })
        {
            return;
        }

        DataPackage package = new();
        package.SetText(file.Name);
        Clipboard.SetContent(package);

        tab.ReportInformation("Copied", $"{file.Name} copied to the clipboard.");
    }
}
