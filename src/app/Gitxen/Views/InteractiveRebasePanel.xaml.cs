// SPDX-License-Identifier: GPL-3.0-or-later
using System.Collections.ObjectModel;
using System.ComponentModel;
using Gitxen.Services;
using Gitxen.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Gitxen.Views;

/// <summary>
///  The interactive rebase editor: the commits after a chosen one, newest at the top, to reorder by
///  dragging and to pick, reword, squash, fix up, drop or stop at. <see cref="ShowAsync"/> returns the
///  plan oldest first, as git applies it, or null when cancelled.
/// </summary>
public sealed partial class InteractiveRebasePanel : UserControl
{
    private readonly ObservableCollection<RebaseRow> _rows = [];
    private List<string> _originalOrder = [];
    private ContentDialog? _dialog;

    public InteractiveRebasePanel()
    {
        InitializeComponent();
        Rows.ItemsSource = _rows;
        _rows.CollectionChanged += (_, _) => Refresh();
    }

    internal static async Task<IReadOnlyList<RebaseStep>?> ShowAsync(XamlRoot root, RebasePlan plan)
    {
        InteractiveRebasePanel panel = new();

        foreach (RebaseCommit commit in plan.Commits.Reverse())
        {
            RebaseRow row = new(commit);
            row.PropertyChanged += panel.OnRowChanged;
            panel._rows.Add(row);
        }

        panel._originalOrder = [.. plan.Commits.Select(commit => commit.Id)];

        if (plan.Pushed > 0)
        {
            panel.Warning.Text = $"{plan.Pushed} of these commits are already pushed. Rewriting them means the branch has to be force-pushed, and anyone who has pulled it will have to deal with that.";
            panel.Warning.Visibility = Visibility.Visible;
        }

        ContentDialog dialog = new()
        {
            Title = $"Edit the commits after {plan.BaseId[..Math.Min(8, plan.BaseId.Length)]}",
            Content = panel,
            PrimaryButtonText = "Rewrite",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = root
        };

        panel._dialog = dialog;
        panel.Refresh();

        return await dialog.ShowAsync() == ContentDialogResult.Primary ? panel.Steps() : null;
    }

    /// <summary>Oldest first: the list shows newest at the top, git applies from the oldest.</summary>
    private List<RebaseStep> Steps() =>
        [.. _rows.Reverse().Select(row => new RebaseStep(row.Commit.Id, row.Action, row.Message))];

    private void OnRowChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(RebaseRow.ActionIndex) or nameof(RebaseRow.Message))
        {
            Refresh();
        }
    }

    private void Refresh()
    {
        List<RebaseStep> steps = Steps();
        string problem = InteractiveRebase.Validate(steps);
        bool reordered = !steps.Select(step => step.CommitId).SequenceEqual(_originalOrder);
        bool changed = reordered || steps.Any(step => step.Action != RebaseAction.Pick);

        int Count(RebaseAction action) => steps.Count(step => step.Action == action);
        List<string> parts = [];
        AddPart(parts, Count(RebaseAction.Reword), "reworded");
        AddPart(parts, Count(RebaseAction.Squash), "squashed");
        AddPart(parts, Count(RebaseAction.Fixup), "fixed up");
        AddPart(parts, Count(RebaseAction.Drop), "dropped");
        AddPart(parts, Count(RebaseAction.Edit), "to stop at");

        if (reordered)
        {
            parts.Add("reordered");
        }

        Summary.Text = problem.Length > 0
            ? problem
            : changed ? $"{steps.Count} commits: {string.Join(", ", parts)}. Undo puts the branch back afterwards." : "Nothing changed yet.";

        if (_dialog is ContentDialog dialog)
        {
            dialog.IsPrimaryButtonEnabled = problem.Length == 0 && changed;
            int rewritten = steps.Count(step => step.Action != RebaseAction.Drop);
            dialog.PrimaryButtonText = rewritten == 1 ? "Rewrite 1 commit" : $"Rewrite {rewritten} commits";
        }

        static void AddPart(List<string> parts, int count, string what)
        {
            if (count > 0)
            {
                parts.Add($"{count} {what}");
            }
        }
    }
}
