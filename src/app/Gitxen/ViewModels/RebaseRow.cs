// SPDX-License-Identifier: GPL-3.0-or-later
using Gitxen.Services;
using Microsoft.UI.Xaml;

namespace Gitxen.ViewModels;

/// <summary>One commit in the interactive rebase editor: what to do with it, and its message if reworded.</summary>
public sealed class RebaseRow : ObservableObject
{
    /// <summary>The choices, in the order the action box shows them; the index is the <see cref="RebaseAction"/>.</summary>
    public static readonly string[] Actions =
    [
        "Pick",
        "Reword",
        "Squash into the one below",
        "Fixup into the one below",
        "Drop",
        "Stop to edit"
    ];

    private int _actionIndex;
    private string _message;

    internal RebaseRow(RebaseCommit commit)
    {
        Commit = commit;
        _message = commit.Message;
    }

    internal RebaseCommit Commit { get; }

    public string ShortId => Commit.ShortId;

    public string Subject => Commit.Subject;

    public string Detail => $"{Commit.Author} · {TimeText.Relative(Commit.Date)}";

    public IReadOnlyList<string> ActionNames => Actions;

    public int ActionIndex
    {
        get => _actionIndex;
        set
        {
            if (SetProperty(ref _actionIndex, value))
            {
                OnPropertyChanged(nameof(MessageVisibility));
                OnPropertyChanged(nameof(SubjectVisibility));
                OnPropertyChanged(nameof(RowOpacity));
            }
        }
    }

    internal RebaseAction Action => (RebaseAction)Math.Clamp(_actionIndex, 0, Actions.Length - 1);

    /// <summary>The full message, edited in place when the commit is reworded.</summary>
    public string Message
    {
        get => _message;
        set => SetProperty(ref _message, value);
    }

    public Visibility MessageVisibility => Action == RebaseAction.Reword ? Visibility.Visible : Visibility.Collapsed;

    public Visibility SubjectVisibility => Action == RebaseAction.Reword ? Visibility.Collapsed : Visibility.Visible;

    /// <summary>A dropped commit fades, so the plan reads at a glance.</summary>
    public double RowOpacity => Action == RebaseAction.Drop ? 0.4 : 1;
}
