// SPDX-License-Identifier: GPL-3.0-or-later
using Gitxen.Models;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media.Animation;

namespace Gitxen.Views;

/// <summary>
///  The quick launcher: one search box over every repository Gitxen knows, and nothing else.
/// </summary>
/// <remarks>
///  <para>
///   It is the start screen, so the app opens on a single question rather than on everything at
///   once, and it comes back over whatever is on screen with Ctrl+K to jump to another repository.
///  </para>
///  <para>
///   It does no git work of its own. The window supplies the repositories and decides what choosing
///   one means; this only finds them.
///  </para>
/// </remarks>
public sealed partial class QuickLaunch : UserControl
{
    private const int MaxSuggestions = 8;

    /// <summary>How long each word stays before the next one decodes in.</summary>
    private static readonly TimeSpan _wordInterval = TimeSpan.FromSeconds(5);

    /// <summary>How quickly the old word is deleted, a letter at a time.</summary>
    private static readonly TimeSpan _deleteDelay = TimeSpan.FromMilliseconds(45);

    /// <summary>The pause between the old word going and the new one starting.</summary>
    private static readonly TimeSpan _pauseDelay = TimeSpan.FromMilliseconds(320);

    /// <summary>The typing pace: this per letter, give or take a little, the way a person types.</summary>
    private const int TypeDelayMs = 95;

    private const int TypeJitterMs = 30;

    /// <summary>
    ///  Everything that reads as "What are we ___ today?". Building comes first and is where every
    ///  start begins; the rest follow in a random order, never the same word twice in a row.
    /// </summary>
    private static readonly string[] _words =
    [
        "building", "cooking", "crafting", "shipping", "forging", "hacking", "designing", "launching",
        "brewing", "sketching", "coding", "creating", "making", "inventing", "engineering", "assembling",
        "composing", "prototyping", "refactoring", "shaping", "weaving", "tinkering with", "sculpting",
        "writing", "polishing", "debugging", "deploying", "releasing", "architecting", "orchestrating",
        "dreaming up", "cooking up", "spinning up", "conjuring", "hatching", "growing", "taming",
        "merging", "rebasing", "committing", "pushing", "baking", "stitching together", "wiring up",
        "piloting", "exploring", "remixing", "igniting", "unleashing", "fine-tuning"
    ];

    private IReadOnlyList<LaunchItem> _shown = [];
    private CancellationTokenSource? _commitSearch;

    private DispatcherQueueTimer? _wordTimer;
    private DispatcherQueueTimer? _typeTimer;
    private string _target = "";
    private bool _deleting;
    private int _wordIndex;

    public QuickLaunch()
    {
        InitializeComponent();

        // From the assembly, so the start screen cannot disagree with the build.
        Version? version = typeof(QuickLaunch).Assembly.GetName().Version;
        BrandVersion.Text = version is null ? "" : version.ToString(3);
    }

    /// <summary>Every repository on offer, most useful first. Asked afresh as the person types.</summary>
    public Func<IReadOnlyList<LaunchItem>>? Source { get; set; }

    /// <summary>
    ///  Commits matching what was typed, from the search index: asked for after a short pause, and
    ///  only once at least three characters are there. Null leaves the launcher repositories-only.
    /// </summary>
    public Func<string, CancellationToken, Task<IReadOnlyList<LaunchItem>>>? CommitSource { get; set; }

    /// <summary>A repository folder was chosen, or a folder path was typed or pasted.</summary>
    public event EventHandler<string>? Chosen;

    /// <summary>A commit was chosen: its repository folder and its full id.</summary>
    public event EventHandler<(string Folder, string CommitId)>? CommitChosen;

    /// <summary>Esc, or a click on the veil: carry on with whatever was there.</summary>
    public event EventHandler? Dismissed;

    /// <summary>What the window uses as its title bar while this covers the real one.</summary>
    public FrameworkElement DragStrip => DragArea;

    public bool IsShowing => Visibility == Visibility.Visible;

    /// <param name="asStartScreen">
    ///  The whole window, plain, when the app starts; a card on a dimmed veil when it is called up over
    ///  work, so it is clear that what was there is still there underneath.
    /// </param>
    public void Show(bool asStartScreen)
    {
        StartBackground.Visibility = asStartScreen ? Visibility.Visible : Visibility.Collapsed;
        Art.Visibility = asStartScreen ? Visibility.Visible : Visibility.Collapsed;
        Brand.Visibility = asStartScreen ? Visibility.Visible : Visibility.Collapsed;
        StartWords();
        OverlayBackground.Visibility = asStartScreen ? Visibility.Collapsed : Visibility.Visible;
        Card.Visibility = asStartScreen ? Visibility.Collapsed : Visibility.Visible;

        SearchBox.Text = "";
        _shown = [];
        SearchBox.ItemsSource = null;
        Visibility = Visibility.Visible;

        // Focus once it is laid out; a collapsed box cannot take it.
        DispatcherQueue.TryEnqueue(() => SearchBox.Focus(FocusState.Programmatic));
    }

    public void Hide()
    {
        _commitSearch?.Cancel();
        SearchBox.IsSuggestionListOpen = false;
        Visibility = Visibility.Collapsed;

        // Nothing keeps running once the launcher is gone: no timers, no drifting illustration.
        StopWords();
        Art.Visibility = Visibility.Collapsed;
    }

    // ---- The changing word ------------------------------------------------------------------------
    // A typewriter: the old word is deleted a letter at a time, a short pause, and the new one is typed
    // at an unhurried, slightly uneven pace behind a caret that fades once it is done. One text block
    // changes, a couple of times a second for about two seconds in every seven; nothing runs while a
    // word rests or once the launcher is gone. With Windows' animation effects off the word simply
    // changes.

    private static bool AnimationsEnabled => new Windows.UI.ViewManagement.UISettings().AnimationsEnabled;

    private Storyboard CaretRest => (Storyboard)Resources["CaretRest"];

    private void StartWords()
    {
        StopWords();

        _wordIndex = 0;
        _target = _words[0];
        Word.Text = _target;
        Caret.Opacity = 0;

        _wordTimer = DispatcherQueue.CreateTimer();
        _wordTimer.Interval = _wordInterval;
        _wordTimer.IsRepeating = false;
        _wordTimer.Tick += (_, _) => NextWord();
        _wordTimer.Start();
    }

    private void StopWords()
    {
        _wordTimer?.Stop();
        _wordTimer = null;
        _typeTimer?.Stop();
        CaretRest.Stop();
    }

    private void NextWord()
    {
        int next = Random.Shared.Next(_words.Length - 1);
        _wordIndex = next >= _wordIndex ? next + 1 : next;
        _target = _words[_wordIndex];

        if (!AnimationsEnabled)
        {
            Word.Text = _target;
            _wordTimer?.Start();
            return;
        }

        if (_typeTimer is null)
        {
            _typeTimer = DispatcherQueue.CreateTimer();
            _typeTimer.IsRepeating = false;
            _typeTimer.Tick += (_, _) => TypeStep();
        }

        CaretRest.Stop();
        Caret.Opacity = 1;
        _deleting = true;
        Schedule(_deleteDelay);
    }

    /// <summary>One keystroke: a backspace while the old word goes, then a letter of the new one.</summary>
    private void TypeStep()
    {
        string shown = Word.Text;

        if (_deleting)
        {
            if (shown.Length > 0)
            {
                Word.Text = shown[..^1];
                Schedule(_deleteDelay);
                return;
            }

            _deleting = false;
            Schedule(_pauseDelay);
            return;
        }

        if (shown.Length < _target.Length)
        {
            Word.Text = _target[..(shown.Length + 1)];
            Schedule(TimeSpan.FromMilliseconds(TypeDelayMs + Random.Shared.Next(-TypeJitterMs, TypeJitterMs + 1)));
            return;
        }

        // Done: let the caret fade, and give the word its full five seconds from here.
        CaretRest.Begin();
        _wordTimer?.Start();
    }

    private void Schedule(TimeSpan delay)
    {
        if (_typeTimer is null)
        {
            return;
        }

        _typeTimer.Interval = delay;
        _typeTimer.Start();
    }

    private void SearchBox_GotFocus(object sender, RoutedEventArgs e) => Suggest(SearchBox.Text);

    private void SearchBox_TextChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args)
    {
        if (args.Reason == AutoSuggestionBoxTextChangeReason.UserInput)
        {
            Suggest(sender.Text);
        }
    }

    /// <summary>
    ///  Ranks the repositories against what has been typed, the way the go-to palette ranks its items:
    ///  prefix, then initials ("gx" finds gitxen), then anywhere in the name or folder. Nothing typed
    ///  lists them as they come, which is open ones first and then most recently used.
    /// </summary>
    private void Suggest(string text)
    {
        IReadOnlyList<LaunchItem> all = Source?.Invoke() ?? [];
        string query = text.Trim();

        _shown = query.Length == 0
            ? [.. all.Take(MaxSuggestions)]
            :
            [
                .. all
                    .Select((item, index) => (Item: item, Index: index, Score: Score(item, query)))
                    .Where(candidate => candidate.Score is not null)
                    .OrderBy(candidate => candidate.Score)
                    .ThenBy(candidate => candidate.Index)
                    .Select(candidate => candidate.Item)
                    .Take(MaxSuggestions)
            ];

        SearchBox.ItemsSource = _shown;
        SearchBox.IsSuggestionListOpen = _shown.Count > 0;

        _ = SearchCommitsAsync(query);
    }

    /// <summary>
    ///  Appends the commits the index finds under the repositories. Each keystroke cancels the last
    ///  search, and nothing is asked until typing pauses, so the list never lags behind the box.
    /// </summary>
    private async Task SearchCommitsAsync(string query)
    {
        if (_commitSearch is CancellationTokenSource previous)
        {
            await previous.CancelAsync();
        }

        CancellationTokenSource cancellation = new();
        _commitSearch = cancellation;

        if (query.Length < 3 || CommitSource is null)
        {
            return;
        }

        try
        {
            await Task.Delay(160, cancellation.Token);
            IReadOnlyList<LaunchItem> commits = await CommitSource(query, cancellation.Token);

            if (cancellation.IsCancellationRequested || commits.Count == 0)
            {
                return;
            }

            _shown = [.. _shown.Where(item => !item.IsCommit), .. commits];
            SearchBox.ItemsSource = _shown;
            SearchBox.IsSuggestionListOpen = true;
        }
        catch (OperationCanceledException)
        {
            // Superseded by a newer keystroke.
        }
    }

    /// <summary>A repository opens its History; a commit opens it at that commit.</summary>
    private void Choose(LaunchItem item)
    {
        if (item.IsCommit)
        {
            CommitChosen?.Invoke(this, (item.Path, item.CommitId));
        }
        else
        {
            Chosen?.Invoke(this, item.Path);
        }
    }

    private static int? Score(LaunchItem item, string query) =>
        new GoToItem("", item.Name, "", item.Path, () => Task.CompletedTask).ScoreFor(query);

    private void SearchBox_QuerySubmitted(AutoSuggestBox sender, AutoSuggestBoxQuerySubmittedEventArgs args)
    {
        if (args.ChosenSuggestion is LaunchItem chosen)
        {
            Choose(chosen);
            return;
        }

        string text = args.QueryText.Trim().Trim('"');

        if (text.Length == 0)
        {
            return;
        }

        // Enter with nothing highlighted takes the best match, as a search engine's does.
        if (_shown.Count > 0)
        {
            Choose(_shown[0]);
            return;
        }

        // A folder pasted in opens even if Gitxen has never seen it.
        if (Directory.Exists(text))
        {
            Chosen?.Invoke(this, text);
        }
    }

    /// <summary>
    ///  Escape closes the suggestions first, then the launcher. It is caught on the way down: the search
    ///  box's own text box marks Escape handled, so a plain KeyDown never arrives while it has focus.
    /// </summary>
    private void Root_PreviewKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key != Windows.System.VirtualKey.Escape)
        {
            return;
        }

        e.Handled = true;

        if (SearchBox.IsSuggestionListOpen)
        {
            SearchBox.IsSuggestionListOpen = false;
            return;
        }

        Dismissed?.Invoke(this, EventArgs.Empty);
    }

    private void Veil_PointerPressed(object sender, PointerRoutedEventArgs e) => Dismissed?.Invoke(this, EventArgs.Empty);
}
