// SPDX-License-Identifier: GPL-3.0-or-later
using Gitxen.Models;
using Gitxen.Services;
using Gitxen.Theming;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Gitxen.Views;

/// <summary>What the shell has to act on once the wizard is finished.</summary>
/// <remarks>
///  Only the repositories and projects: everything else the wizard collects is a setting, and it has
///  already written those to <see cref="AppOptions"/> and to git's global configuration. Creating
///  groups and opening repositories is the shell's job, so those come back as data instead.
/// </remarks>
/// <param name="Repositories">Repositories to add to Home, with their project.</param>
/// <param name="Projects">Projects to create, with the colour they were proposed in.</param>
/// <param name="Layout">Tabs or the repository column.</param>
/// <param name="Mode">Simple or Advanced.</param>
public sealed record SetupOutcome(
    IReadOnlyList<SetupImport> Repositories,
    IReadOnlyList<ProposedProject> Projects,
    RepositoryLayout Layout,
    UiMode Mode);

/// <summary>
///  The first-run wizard: identity, appearance, behaviour, and importing the repositories that are
///  already on the machine.
/// </summary>
/// <remarks>
///  <para>
///   A first run currently drops someone into an empty shell with a Home page that says nothing, no
///   git identity (so the first commit fails), and no idea that projects or the layout options
///   exist. This walks through exactly those things once.
///  </para>
///  <para>
///   Steps are panels switched by visibility rather than pages in a Frame, for the same reason the
///   repository sections are: going back a step must not discard what was typed into the step you
///   are returning to.
///  </para>
/// </remarks>
public sealed partial class SetupWizardView : UserControl
{
    private const int LastStep = 5;
    private const int RepositoriesStepIndex = 4;

    /// <summary>The theme entries, in the order the dropdown lists them.</summary>
    private IReadOnlyList<ThemeOption> _themes = [];

    private GlobalGitSettings? _git;
    private bool _scanStarted;
    private int _step;

    public SetupWizardView()
    {
        InitializeComponent();
    }

    /// <summary>Raised when the theme is changed, so the shell can apply it while the wizard is open.</summary>
    public event EventHandler? ThemeChanged;

    /// <summary>Raised when the wizard finishes. Skipping raises it with nothing to import.</summary>
    public event EventHandler<SetupOutcome>? Completed;

    /// <summary>
    ///  Supplies what the wizard cannot reach on its own.
    /// </summary>
    /// <param name="git">Reads and writes the global git configuration.</param>
    /// <param name="mode">The mode in force, so the wizard offers it rather than overriding it.</param>
    /// <param name="pickFolder">
    ///  Shows the folder picker. It has to be initialised with the window handle, which a
    ///  <see cref="UserControl"/> does not have, so the shell provides it.
    /// </param>
    /// <remarks>
    ///  Internal because <see cref="GlobalGitSettings"/> is: the services in this project are not
    ///  part of its public surface, and only the shell calls this.
    /// </remarks>
    /// <param name="knownPaths">Repositories already on Home, which the finder lists but does not add again.</param>
    /// <param name="projectNames">Existing projects, offered as places to put new repositories.</param>
    internal void Initialize(
        GlobalGitSettings git,
        UiMode mode,
        Func<Task<string?>> pickFolder,
        IEnumerable<string> knownPaths,
        IEnumerable<string> projectNames)
    {
        _git = git;
        Importer.Initialize(pickFolder, knownPaths, projectNames);

        SidebarLayoutOption.IsChecked = AppOptions.Layout == RepositoryLayout.Sidebar;
        TabsLayoutOption.IsChecked = AppOptions.Layout != RepositoryLayout.Sidebar;
        AdvancedModeOption.IsChecked = mode == UiMode.Advanced;
        SimpleModeOption.IsChecked = mode != UiMode.Advanced;

        // A dropdown here rather than the settings page's gallery: the wizard is a narrow column of
        // one-line decisions, and the theme can be looked at properly in Settings afterwards.
        _themes = ThemeOption.Build(ThemeService.IsSystemDark);
        ThemeBox.ItemsSource = _themes.Select(option => option.Name);
        ThemeBox.SelectedIndex = Math.Max(0, _themes.ToList().FindIndex(
            option => option.Theme == AppOptions.Theme));

        SelectByTag(AutoFetchBox, AppOptions.AutoFetchMinutes.ToString(), fallbackIndex: 2);
        SelectByTag(CommitsBox, AppOptions.MaxCommits.ToString(), fallbackIndex: 1);

        _ = LoadGitDetailsAsync();
    }

    /// <summary>
    ///  Reads git's version and the existing global identity.
    /// </summary>
    /// <remarks>
    ///  Off the UI thread: each of these starts a git process, and three of them on the dispatcher is
    ///  a visible stall on the wizard's very first frame.
    /// </remarks>
    private async Task LoadGitDetailsAsync()
    {
        if (_git is not GlobalGitSettings git)
        {
            return;
        }

        (string version, string name, string email, string branch) = await Task.Run(() =>
            (git.GetVersion(), git.Get("user.name"), git.Get("user.email"), git.Get("init.defaultBranch")));

        if (version.Length > 0)
        {
            GitStatusGlyph.Glyph = "\uE73E";
            GitStatusText.Text = "git is installed";
            GitStatusDetail.Text = version;
        }
        else
        {
            GitStatusGlyph.Glyph = "\uE783";
            GitStatusText.Text = "git was not found";
            GitStatusDetail.Text =
                "Gitxen runs the real git, so almost nothing will work until it is installed and on PATH. "
                + "You can finish this wizard and install it afterwards.";
        }

        UserNameBox.Text = name;
        UserEmailBox.Text = email;

        if (branch.Length > 0)
        {
            DefaultBranchBox.Text = branch;
        }
    }

    // ---- Navigation ------------------------------------------------------------------------------

    private void Back_Click(object sender, RoutedEventArgs e) => GoTo(PreviousStep());

    private async void Next_Click(object sender, RoutedEventArgs e)
    {
        if (_step == LastStep)
        {
            await FinishAsync();
            return;
        }

        GoTo(NextStep());
    }

    private void Skip_Click(object sender, RoutedEventArgs e)
    {
        // Nothing is written and nothing is imported, but the wizard is still marked done by the
        // shell: being asked again on every launch after saying no is the worst of both.
        Importer.CancelScans();
        Completed?.Invoke(this, new SetupOutcome([], [], AppOptions.Layout, CurrentMode()));
    }

    private int NextStep() => _step + 1;

    private int PreviousStep() => _step - 1;

    private void GoTo(int step)
    {
        _step = Math.Clamp(step, 0, LastStep);

        WelcomeStep.Visibility = VisibleOn(0);
        IdentityStep.Visibility = VisibleOn(1);
        AppearanceStep.Visibility = VisibleOn(2);
        BehaviourStep.Visibility = VisibleOn(3);
        RepositoriesStep.Visibility = VisibleOn(RepositoriesStepIndex);
        FinishStep.Visibility = VisibleOn(LastStep);

        (StepTitle.Text, StepSubtitle.Text) = _step switch
        {
            0 => ("Welcome to Gitxen", "A few questions, then you are set up."),
            1 => ("Who are your commits from?", "Written to your global git configuration."),
            2 => ("How should it look?", "All of this can be changed later in Settings."),
            3 => ("How should it behave?", "Two settings that are easier to choose now than to find later."),
            RepositoriesStepIndex => ("Your repositories", "Found in the usual places. Pick what goes on Home."),
            _ => ("Ready", "Here is what will happen.")
        };

        StepProgress.Value = _step;
        BackButton.IsEnabled = _step > 0;
        NextButton.Content = _step == LastStep ? "Finish" : "Next";
        SkipButton.Visibility = _step == LastStep ? Visibility.Collapsed : Visibility.Visible;

        if (_step == 1)
        {
            IdentityWarning.IsOpen = UserNameBox.Text.Trim().Length == 0 || UserEmailBox.Text.Trim().Length == 0;
        }

        // The usual code folders are scanned as soon as the step is reached, so it opens with
        // something to choose from rather than an empty list and a button.
        if (_step == RepositoriesStepIndex && !_scanStarted)
        {
            _scanStarted = true;
            Importer.ScanSuggested();
        }

        if (_step == LastStep)
        {
            BuildSummary();
        }
    }

    private Visibility VisibleOn(int index) =>
        _step == index ? Visibility.Visible : Visibility.Collapsed;

    // ---- Appearance ------------------------------------------------------------------------------

    private void Theme_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        // Guard the call raised while the control is still being constructed, before Initialize.
        if (!IsLoaded && _git is null)
        {
            return;
        }

        AppOptions.Theme = ThemeBox.SelectedIndex >= 0 && ThemeBox.SelectedIndex < _themes.Count
            ? _themes[ThemeBox.SelectedIndex].Theme
            : AppTheme.System;

        ThemeChanged?.Invoke(this, EventArgs.Empty);
    }

    // ---- Finishing -------------------------------------------------------------------------------

    private void BuildSummary()
    {
        SummaryPanel.Children.Clear();

        AddSummaryLine("\uE77B", UserNameBox.Text.Trim().Length > 0 || UserEmailBox.Text.Trim().Length > 0
            ? $"Commits from {UserNameBox.Text.Trim()} <{UserEmailBox.Text.Trim()}>"
            : "No git identity set — you will be asked again before your first commit");

        AddSummaryLine("\uE771", $"{ThemeName()} theme, {(SidebarLayoutOption.IsChecked == true ? "repository column" : "tabs")}, "
            + $"{(AdvancedModeOption.IsChecked == true ? "Advanced" : "Simple")} mode");

        int minutes = TagValue(AutoFetchBox, AppOptions.AutoFetchMinutes);
        AddSummaryLine("\uE895", minutes == 0
            ? "No background fetching"
            : $"Fetching every {minutes} minutes");

        AddSummaryLine("\uE8B7", Importer.Describe());
    }

    private void AddSummaryLine(string glyph, string text)
    {
        StackPanel row = new() { Orientation = Orientation.Horizontal, Spacing = 12 };

        row.Children.Add(new FontIcon
        {
            Glyph = glyph,
            FontSize = 16,
            VerticalAlignment = VerticalAlignment.Center
        });

        row.Children.Add(new TextBlock
        {
            Text = text,
            TextWrapping = TextWrapping.Wrap,
            VerticalAlignment = VerticalAlignment.Center
        });

        SummaryPanel.Children.Add(row);
    }

    private UiMode CurrentMode() =>
        AdvancedModeOption.IsChecked == true ? UiMode.Advanced : UiMode.Simple;

    private string ThemeName() => ThemeBox.SelectedIndex switch
    {
        1 => "Light",
        2 => "Dark",
        _ => "System"
    };

    private async Task FinishAsync()
    {
        NextButton.IsEnabled = false;
        BackButton.IsEnabled = false;

        // The layout and the mode are applied by the shell through its view model, which is what
        // raises the notifications the strip and the column are bound to; writing AppOptions here
        // would change the value without anything redrawing.
        AppOptions.AutoFetchMinutes = TagValue(AutoFetchBox, AppOptions.AutoFetchMinutes);
        AppOptions.MaxCommits = TagValue(CommitsBox, AppOptions.MaxCommits);

        await WriteGitConfigurationAsync();

        Importer.CancelScans();
        ImportPlan plan = Importer.BuildPlan();

        Completed?.Invoke(this, new SetupOutcome(
            plan.Repositories,
            plan.Projects,
            SidebarLayoutOption.IsChecked == true ? RepositoryLayout.Sidebar : RepositoryLayout.Tabs,
            CurrentMode()));
    }

    /// <summary>
    ///  Writes the identity to git's global configuration.
    /// </summary>
    /// <remarks>
    ///  Failures are deliberately not reported. This runs three <c>git config</c> writes as the last
    ///  act of a wizard that is about to close; a modal error about one of them, at that moment, tells
    ///  someone nothing they can act on. The values are visible and editable afterwards in Settings,
    ///  where a failure to write does surface.
    /// </remarks>
    private async Task WriteGitConfigurationAsync()
    {
        if (_git is not GlobalGitSettings git)
        {
            return;
        }

        string name = UserNameBox.Text.Trim();
        string email = UserEmailBox.Text.Trim();
        string branch = DefaultBranchBox.Text.Trim();

        await Task.Run(() =>
        {
            // Only write what was actually filled in: clearing an existing global identity because a
            // box was left empty would be a destructive answer to a question nobody asked.
            if (name.Length > 0)
            {
                git.Set("user.name", name);
            }

            if (email.Length > 0)
            {
                git.Set("user.email", email);
            }

            if (branch.Length > 0)
            {
                git.Set("init.defaultBranch", branch);
            }
        });
    }

    // ---- Helpers ---------------------------------------------------------------------------------

    private static void SelectByTag(ComboBox box, string tag, int fallbackIndex)
    {
        for (int index = 0; index < box.Items.Count; index++)
        {
            if (box.Items[index] is ComboBoxItem item && (item.Tag as string) == tag)
            {
                box.SelectedIndex = index;
                return;
            }
        }

        box.SelectedIndex = fallbackIndex;
    }

    private static int TagValue(ComboBox box, int fallback) =>
        box.SelectedItem is ComboBoxItem item
        && item.Tag is string tag
        && int.TryParse(tag, out int value)
            ? value
            : fallback;
}
