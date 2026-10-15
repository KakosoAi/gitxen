// SPDX-License-Identifier: GPL-3.0-or-later
using Gitxen.GitHub;
using Gitxen.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Gitxen.Views;

/// <summary>What the New pull request dialog collected.</summary>
internal sealed record NewPullRequestRequest(string Title, string Body, string BaseBranch, string Head, bool Draft);

/// <summary>
///  The prompts the GitHub pages ask before doing something: a title, a comment, a merge method.
/// </summary>
/// <remarks>
///  Each returns null when cancelled, so a caller can tell that apart from an empty answer, and none
///  of them lets the primary button be pressed until what the action cannot go without has been
///  filled in — GitHub refuses a pull request with no title, and finding that out from an error bar is
///  a worse way to learn it than a button that is not yet available.
/// </remarks>
internal static class GitHubDialogs
{
    /// <param name="write">
    ///  Writes a title and description for (base, head) with Claude; null hides the button. It is only
    ///  called when the user presses it, since it sends the branch's commits and diff to Anthropic.
    /// </param>
    public static async Task<NewPullRequestRequest?> NewPullRequestAsync(
        XamlRoot root,
        string head,
        string defaultBase,
        Func<string, string, Task<(string Title, string Body)>>? write = null)
    {
        TextBox title = new() { Header = "Title", PlaceholderText = "What does this change?" };
        TextBox body = MultiLine("Description (optional)", 130);
        TextBox baseBranch = new() { Header = "Merge into", Text = defaultBase };
        TextBox from = new() { Header = "From", Text = head };
        CheckBox draft = new() { Content = "Open as a draft" };

        TextBlock note = new()
        {
            Text = "The branch has to be on GitHub already. Push it first if it is not.",
            TextWrapping = TextWrapping.Wrap,
            Opacity = 0.7
        };

        StackPanel panel = Panel(title, body, baseBranch, from, draft, note);

        if (write is not null)
        {
            Button writeButton = new() { Content = "Write title and description with AI", HorizontalAlignment = HorizontalAlignment.Right };
            ToolTipService.SetToolTip(writeButton, "Have Claude write them from the branch's commits and diff (sends them to Anthropic)");
            writeButton.Click += (_, _) => _ = WriteAsync();

            async Task WriteAsync()
            {
                writeButton.IsEnabled = false;
                writeButton.Content = "Writing…";

                try
                {
                    (string writtenTitle, string writtenBody) = await write(baseBranch.Text.Trim(), from.Text.Trim());
                    title.Text = writtenTitle;
                    body.Text = writtenBody;
                }
                catch (Gitxen.Services.AiException ex)
                {
                    note.Text = ex.Message;
                }
                finally
                {
                    writeButton.IsEnabled = true;
                    writeButton.Content = "Write title and description with AI";
                }
            }

            panel.Children.Insert(0, writeButton);
        }

        ContentDialog dialog = Dialog(root, "New pull request", panel, "Open pull request");
        dialog.IsPrimaryButtonEnabled = false;

        void Refresh() => dialog.IsPrimaryButtonEnabled =
            title.Text.Trim().Length > 0 && baseBranch.Text.Trim().Length > 0 && from.Text.Trim().Length > 0;

        title.TextChanged += (_, _) => Refresh();
        baseBranch.TextChanged += (_, _) => Refresh();
        from.TextChanged += (_, _) => Refresh();

        return await dialog.ShowAsync() == ContentDialogResult.Primary
            ? new NewPullRequestRequest(title.Text.Trim(), body.Text, baseBranch.Text.Trim(), from.Text.Trim(), draft.IsChecked == true)
            : null;
    }

    public static async Task<(string Title, string Body)?> NewIssueAsync(XamlRoot root)
    {
        TextBox title = new() { Header = "Title", PlaceholderText = "What is the problem?" };
        TextBox body = MultiLine("Description (optional)", 160);

        ContentDialog dialog = Dialog(root, "New issue", Panel(title, body), "Open issue");
        dialog.IsPrimaryButtonEnabled = false;
        title.TextChanged += (_, _) => dialog.IsPrimaryButtonEnabled = title.Text.Trim().Length > 0;

        return await dialog.ShowAsync() == ContentDialogResult.Primary ? (title.Text.Trim(), body.Text) : null;
    }

    /// <summary>A comment box. Empty is not accepted.</summary>
    public static async Task<string?> CommentAsync(XamlRoot root, string title, string accept = "Comment")
    {
        TextBox body = MultiLine("Comment", 180);

        ContentDialog dialog = Dialog(root, title, Panel(body), accept);
        dialog.IsPrimaryButtonEnabled = false;
        body.TextChanged += (_, _) => dialog.IsPrimaryButtonEnabled = body.Text.Trim().Length > 0;

        return await dialog.ShowAsync() == ContentDialogResult.Primary ? body.Text : null;
    }

    public static async Task<(ReviewAction Action, string Body)?> ReviewAsync(XamlRoot root, int number)
    {
        RadioButtons kind = new()
        {
            Header = "Your review",
            ItemsSource = new[] { "Approve", "Request changes", "Comment only" },
            SelectedIndex = 0
        };

        TextBox body = MultiLine("Message", 130);

        ContentDialog dialog = Dialog(root, $"Review #{number}", Panel(kind, body), "Submit review");

        // Approving needs no words; asking for changes or only commenting does.
        void Refresh() => dialog.IsPrimaryButtonEnabled = kind.SelectedIndex == 0 || body.Text.Trim().Length > 0;

        kind.SelectionChanged += (_, _) => Refresh();
        body.TextChanged += (_, _) => Refresh();

        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
        {
            return null;
        }

        ReviewAction action = kind.SelectedIndex switch
        {
            1 => ReviewAction.RequestChanges,
            2 => ReviewAction.Comment,
            _ => ReviewAction.Approve
        };

        return (action, body.Text);
    }

    public static async Task<(MergeMethod Method, bool DeleteBranch)?> MergeAsync(XamlRoot root, PullRequestRow row, MergeMethod method)
    {
        RadioButtons kind = new()
        {
            Header = "How to merge",
            ItemsSource = new[]
            {
                "Merge commit — keep every commit, and add a merge commit",
                "Squash — combine everything into one commit",
                "Rebase — replay the commits onto the base, with no merge commit"
            },
            SelectedIndex = (int)method
        };

        CheckBox delete = new() { Content = $"Delete the branch {row.Model.HeadBranch} afterwards" };

        TextBlock warning = new()
        {
            Text = "Merging changes the base branch on GitHub and cannot be undone from here.",
            TextWrapping = TextWrapping.Wrap,
            Opacity = 0.7
        };

        ContentDialog dialog = Dialog(root, $"Merge #{row.Number}?", Panel(kind, delete, warning), "Merge");

        return await dialog.ShowAsync() == ContentDialogResult.Primary
            ? ((MergeMethod)Math.Max(0, kind.SelectedIndex), delete.IsChecked == true)
            : null;
    }

    /// <summary>Why an issue is being closed: done, or not going to be done.</summary>
    public static async Task<string?> CloseIssueAsync(XamlRoot root, IssueRow row)
    {
        RadioButtons reason = new()
        {
            Header = "Close as",
            ItemsSource = new[] { "Completed", "Not planned" },
            SelectedIndex = 0
        };

        ContentDialog dialog = Dialog(root, $"Close #{row.Number}?", Panel(reason), "Close issue");

        return await dialog.ShowAsync() == ContentDialogResult.Primary
            ? (reason.SelectedIndex == 1 ? "not planned" : "completed")
            : null;
    }

    public static async Task<(Workflow Workflow, string Branch)?> RunWorkflowAsync(XamlRoot root, IReadOnlyList<Workflow> workflows, string branch)
    {
        ComboBox picker = new()
        {
            Header = "Workflow",
            ItemsSource = workflows.Select(workflow => workflow.Name).ToList(),
            SelectedIndex = 0,
            HorizontalAlignment = HorizontalAlignment.Stretch
        };

        TextBox on = new() { Header = "On branch", Text = branch };

        TextBlock note = new()
        {
            Text = "Only workflows that declare a workflow_dispatch trigger can be started by hand.",
            TextWrapping = TextWrapping.Wrap,
            Opacity = 0.7
        };

        ContentDialog dialog = Dialog(root, "Run workflow", Panel(picker, on, note), "Run");
        on.TextChanged += (_, _) => dialog.IsPrimaryButtonEnabled = on.Text.Trim().Length > 0;

        return await dialog.ShowAsync() == ContentDialogResult.Primary && picker.SelectedIndex >= 0
            ? (workflows[picker.SelectedIndex], on.Text.Trim())
            : null;
    }

    // ---- Building blocks ----

    private static TextBox MultiLine(string header, double height) => new()
    {
        Header = header,
        AcceptsReturn = true,
        TextWrapping = TextWrapping.Wrap,
        Height = height,
        IsSpellCheckEnabled = true
    };

    private static StackPanel Panel(params UIElement[] children)
    {
        StackPanel panel = new() { Spacing = 12, Width = 480 };

        foreach (UIElement child in children)
        {
            panel.Children.Add(child);
        }

        return panel;
    }

    private static ContentDialog Dialog(XamlRoot root, string title, UIElement content, string accept) => new()
    {
        Title = title,
        Content = content,
        PrimaryButtonText = accept,
        CloseButtonText = "Cancel",
        DefaultButton = ContentDialogButton.Primary,
        XamlRoot = root
    };
}
