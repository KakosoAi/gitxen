// SPDX-License-Identifier: GPL-3.0-or-later
using System.Collections.Specialized;
using Gitxen.Models;
using Gitxen.Services;
using Gitxen.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.ApplicationModel.DataTransfer;

namespace Gitxen.Views;

/// <summary>
///  The start page: every repository Gitxen knows, by project, and the ways to add more.
/// </summary>
/// <remarks>
///  <para>
///   One job — find a repository and open it. The actions are icons on a slim toolbar with their names
///   in tooltips, the projects are a column on the left that narrows the list, and each repository is
///   one line: name, last commit, branch. Settings, the layout choice and the shortcut list used to
///   share the page; they are a click away on the toolbar, in Settings, and under F1.
///  </para>
///  <para>
///   With nothing added yet the page is an invitation instead: the three ways to get a repository,
///   over the illustration.
///  </para>
/// </remarks>
public sealed partial class HomeView : UserControl
{
    public static readonly DependencyProperty ViewModelProperty = DependencyProperty.Register(
        nameof(ViewModel), typeof(MainViewModel), typeof(HomeView), new PropertyMetadata(null, OnViewModelChanged));

    /// <summary>
    ///  The repository being dragged.
    /// </summary>
    /// <remarks>
    ///  Held in a field rather than put in the DataPackage: the drop needs the RecentRepository object
    ///  itself, and a DataPackage carries text and storage items, not arbitrary objects. The drag never
    ///  leaves this window, so there is nothing for another application to receive.
    /// </remarks>
    private RecentRepository? _dragging;

    /// <summary>The project the list is narrowed to: null for all, or a project, or the ungrouped bucket.</summary>
    private RepositoryGroup? _selectedProject;

    /// <summary>Set while the project column is being refilled, so that is not read as a click.</summary>
    private bool _refillingProjects;

    private bool _rebuildQueued;

    /// <summary>How many of the most recently used are repeated at the top of "All repositories".</summary>
    private const int RecentlyUsedCount = 4;

    public HomeView()
    {
        InitializeComponent();
    }

    /// <summary>Raised when the page wants the shell to run one of its actions.</summary>
    public event EventHandler<HomeAction>? ActionRequested;

    /// <summary>Raised with the working directory the user picked from the recent list.</summary>
    public event EventHandler<string>? OpenRequested;

    public MainViewModel? ViewModel
    {
        get => (MainViewModel?)GetValue(ViewModelProperty);
        set => SetValue(ViewModelProperty, value);
    }

    /// <summary>Re-reads the recent list and its per-repository detail.</summary>
    public async Task RefreshAsync()
    {
        if (ViewModel is not MainViewModel viewModel)
        {
            return;
        }

        await viewModel.RefreshRecentAsync();
        UpdateBindings();
        await ShowTechnologiesAsync(viewModel);
    }

    /// <summary>
    ///  Reads what each repository is built with, off the UI thread and one at a time, and puts the
    ///  badges on its row as they come. Remembered per folder, so a second refresh costs nothing.
    /// </summary>
    private static async Task ShowTechnologiesAsync(MainViewModel viewModel)
    {
        foreach (RecentRepository entry in viewModel.RecentRepositories.ToList())
        {
            if (entry.IsMissing || entry.Technologies.Count > 0)
            {
                continue;
            }

            string path = entry.Path;
            RepositoryProfile profile = await Task.Run(() => RepositoryInsight.Read(path));
            entry.Technologies = [.. profile.Technologies.Select(technology => new TechBadge(technology))];
        }
    }

    private static void OnViewModelChanged(DependencyObject sender, DependencyPropertyChangedEventArgs args)
    {
        HomeView view = (HomeView)sender;

        if (args.OldValue is MainViewModel old)
        {
            old.RecentRepositories.CollectionChanged -= view.OnListsChanged;
            old.Groups.CollectionChanged -= view.OnListsChanged;
        }

        // Home hosts the global git configuration page with no repository, so it is handed the
        // writer that exists without one.
        if (view.ViewModel is MainViewModel viewModel)
        {
            view.GlobalConfig.GlobalGit = viewModel.GlobalGit;
            viewModel.RecentRepositories.CollectionChanged += view.OnListsChanged;
            viewModel.Groups.CollectionChanged += view.OnListsChanged;
        }

        view.Rebuild();
    }

    /// <summary>
    ///  The lists change a row at a time while they are being refilled, so the page is rebuilt once
    ///  the burst is over rather than once per row.
    /// </summary>
    private void OnListsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (_rebuildQueued)
        {
            return;
        }

        _rebuildQueued = true;
        DispatcherQueue.TryEnqueue(() =>
        {
            _rebuildQueued = false;
            Rebuild();
        });
    }

    /// <summary>
    ///  Refills the project column and the repository list from the view model, keeping the chosen
    ///  project and the filter.
    /// </summary>
    private void Rebuild()
    {
        if (ViewModel is not MainViewModel viewModel)
        {
            return;
        }

        bool any = viewModel.RecentRepositories.Count > 0;

        ListArea.Visibility = any ? Visibility.Visible : Visibility.Collapsed;
        EmptyState.Visibility = any ? Visibility.Collapsed : Visibility.Visible;

        // A project deleted while it was chosen falls back to everything.
        if (_selectedProject is not null
            && !ReferenceEquals(_selectedProject, viewModel.Ungrouped)
            && !viewModel.Groups.Contains(_selectedProject))
        {
            _selectedProject = null;
        }

        Brush neutral = GroupPalette.Accent("Slate");

        List<ProjectRailItem> rail =
        [
            new("All repositories", "\uE8F1", neutral, viewModel.RecentRepositories.Count, null, isProject: false),
            .. viewModel.Groups.Select(group => new ProjectRailItem(group.Name, group.Glyph, group.Accent, group.Count, group, isProject: true))
        ];

        if (viewModel.Groups.Count > 0 && viewModel.Ungrouped.Count > 0)
        {
            rail.Add(new ProjectRailItem("Not in a project", "\uE8B7", neutral, viewModel.Ungrouped.Count, viewModel.Ungrouped, isProject: false));
        }

        _refillingProjects = true;
        ProjectList.ItemsSource = rail;
        ProjectList.SelectedItem = rail.FirstOrDefault(item => ReferenceEquals(item.Target, _selectedProject)) ?? rail[0];
        _refillingProjects = false;

        string filter = FilterBox.Text.Trim();

        bool Matches(RecentRepository entry) =>
            filter.Length == 0
            || entry.Name.Contains(filter, StringComparison.OrdinalIgnoreCase)
            || entry.Path.Contains(filter, StringComparison.OrdinalIgnoreCase)
            || entry.Branch.Contains(filter, StringComparison.OrdinalIgnoreCase);

        List<HomeSection> sections = [];

        if (_selectedProject is RepositoryGroup chosen)
        {
            string title = ReferenceEquals(chosen, viewModel.Ungrouped) ? "Not in a project" : chosen.Name;
            sections.Add(new HomeSection(title, chosen.Repositories.Where(Matches)));
        }
        else
        {
            // Worth repeating only when the list is long enough to have to look for them.
            if (filter.Length == 0 && viewModel.RecentRepositories.Count > RecentlyUsedCount + 2)
            {
                sections.Add(new HomeSection("Recently used", viewModel.RecentRepositories.Take(RecentlyUsedCount)));
            }

            foreach (RepositoryGroup group in viewModel.Groups)
            {
                sections.Add(new HomeSection(group.Name, group.Repositories.Where(Matches)));
            }

            sections.Add(new HomeSection(
                viewModel.Groups.Count > 0 ? "Not in a project" : "Repositories",
                viewModel.Ungrouped.Repositories.Where(Matches)));
        }

        sections.RemoveAll(section => section.Items.Count == 0);

        ((CollectionViewSource)Resources["SectionsSource"]).Source = sections;
        NoMatches.Visibility = any && sections.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void ProjectList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_refillingProjects)
        {
            return;
        }

        _selectedProject = (ProjectList.SelectedItem as ProjectRailItem)?.Target;
        Rebuild();
    }

    private void Filter_TextChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args) => Rebuild();

    /// <summary>Enter in the filter opens the first repository it left showing.</summary>
    private void Filter_QuerySubmitted(AutoSuggestBox sender, AutoSuggestBoxQuerySubmittedEventArgs args)
    {
        if (((CollectionViewSource)Resources["SectionsSource"]).Source is List<HomeSection> sections
            && sections.SelectMany(section => section.Items).FirstOrDefault() is RecentRepository first)
        {
            RequestOpen(first);
        }
    }

    /// <summary>A row's menu button only shows while the pointer is over the row.</summary>
    private void Row_PointerEntered(object sender, PointerRoutedEventArgs e) => SetRowButtonOpacity(sender, 1);

    private void Row_PointerExited(object sender, PointerRoutedEventArgs e) => SetRowButtonOpacity(sender, 0);

    private static void SetRowButtonOpacity(object sender, double opacity)
    {
        if (sender is Grid { Children.Count: > 0 } row && row.Children[^1] is Button button)
        {
            button.Opacity = opacity;
        }
    }

    /// <summary>The repository a menu item or button belongs to: its tag, or failing that its data context.</summary>
    private static RecentRepository? EntryOf(object sender) =>
        (sender as FrameworkElement)?.Tag as RecentRepository ?? (sender as FrameworkElement)?.DataContext as RecentRepository;

    private void Rail_DragOver(object sender, DragEventArgs e)
    {
        // "All repositories" is not somewhere a repository can be moved to.
        bool target = (sender as FrameworkElement)?.Tag is ProjectRailItem { Target: not null };

        e.AcceptedOperation = _dragging is not null && target ? DataPackageOperation.Move : DataPackageOperation.None;
        e.DragUIOverride.Caption = "Move to this project";
        e.DragUIOverride.IsGlyphVisible = false;
    }

    private void Rail_Drop(object sender, DragEventArgs e)
    {
        if (ViewModel is not MainViewModel viewModel || (sender as FrameworkElement)?.Tag is not ProjectRailItem { Target: RepositoryGroup target })
        {
            return;
        }

        MoveDragged(ReferenceEquals(target, viewModel.Ungrouped) ? null : target);
    }

    // ---- Sub-pages -------------------------------------------------------------------------------
    // Application settings and the global git configuration open as pages over Home, swapped by
    // visibility like every other section in the app. Each is populated on entry rather than on
    // load, so Home never runs git it is not showing.

    private async void OpenAppSettings_Click(object sender, RoutedEventArgs e)
    {
        ShowPage(SettingsPage);
        await AppSettings.ActivateAsync();
    }

    private async void OpenGitConfig_Click(object sender, RoutedEventArgs e)
    {
        ShowPage(GitConfigPage);
        await GlobalConfig.ActivateAsync();
    }

    private void BackToHome_Click(object sender, RoutedEventArgs e) => ShowPage(null);

    /// <summary>Shows one sub-page, or the Home content itself when handed null.</summary>
    private void ShowPage(UIElement? page)
    {
        SettingsPage.Visibility = ReferenceEquals(page, SettingsPage) ? Visibility.Visible : Visibility.Collapsed;
        GitConfigPage.Visibility = ReferenceEquals(page, GitConfigPage) ? Visibility.Visible : Visibility.Collapsed;
        MainContent.Visibility = page is null ? Visibility.Visible : Visibility.Collapsed;

        // Behind Home itself, not behind a settings form.
        BackgroundArt.Visibility = page is null ? Visibility.Visible : Visibility.Collapsed;
    }

    private void Open_Click(object sender, RoutedEventArgs e) =>
        ActionRequested?.Invoke(this, HomeAction.OpenRepository);

    private void Clone_Click(object sender, RoutedEventArgs e) =>
        ActionRequested?.Invoke(this, HomeAction.Clone);

    private void Setup_Click(object sender, RoutedEventArgs e) =>
        ActionRequested?.Invoke(this, HomeAction.AddRepositories);

    private void AppSettings_SetupRequested(object? sender, EventArgs e) =>
        ActionRequested?.Invoke(this, HomeAction.RunSetup);

    private void Init_Click(object sender, RoutedEventArgs e) =>
        ActionRequested?.Invoke(this, HomeAction.Initialise);

    /// <summary>Refresh also reads what the repositories are built with again, in case that changed.</summary>
    private async void RefreshRecent_Click(object sender, RoutedEventArgs e)
    {
        foreach (RecentRepository entry in ViewModel?.RecentRepositories ?? [])
        {
            RepositoryInsight.Forget(entry.Path);
            entry.Technologies = [];
        }

        await RefreshAsync();
    }

    private void Recent_ItemClick(object sender, ItemClickEventArgs e) =>
        RequestOpen(e.ClickedItem as RecentRepository);

    private void OpenRecent_Click(object sender, RoutedEventArgs e) => RequestOpen(EntryOf(sender));

    private void RequestOpen(RecentRepository? entry)
    {
        if (entry is not null)
        {
            OpenRequested?.Invoke(this, entry.Path);
        }
    }

    private void CopyPath_Click(object sender, RoutedEventArgs e)
    {
        if (EntryOf(sender) is not RecentRepository entry)
        {
            return;
        }

        DataPackage package = new();
        package.SetText(entry.Path);
        Clipboard.SetContent(package);
    }

    private void RemoveRecent_Click(object sender, RoutedEventArgs e)
    {
        if (EntryOf(sender) is RecentRepository entry)
        {
            // Through the removal that also closes an open tab — an open tab would put a row
            // straight back into the repository column and the strip.
            ViewModel?.RemoveRepository(entry.Path);
            UpdateBindings();
        }
    }

    /// <summary>Redraws the page after the shell changed the lists.</summary>
    /// <remarks>
    ///  Only the list is redrawn. The page has no bindings of its own any more (everything it shows is
    ///  filled in by <see cref="Rebuild"/>), so the generated Bindings object is not created, and
    ///  calling it threw during startup and cut the session restore short.
    /// </remarks>
    public void UpdateBindings() => Rebuild();

    // ---- Projects --------------------------------------------------------------------------------

    private void Repositories_DragItemsStarting(object sender, DragItemsStartingEventArgs e)
    {
        _dragging = e.Items.FirstOrDefault() as RecentRepository;

        // Something has to be offered or the drop targets never light up.
        e.Data.SetText(_dragging?.Path ?? "");
        e.Data.RequestedOperation = DataPackageOperation.Move;
    }

    private void MoveDragged(RepositoryGroup? target)
    {
        if (ViewModel is not MainViewModel viewModel || _dragging is not RecentRepository dragged)
        {
            return;
        }

        _dragging = null;
        viewModel.AssignToGroup(dragged.Path, target);
        UpdateBindings();
    }

    private async void NewGroup_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel is not MainViewModel viewModel)
        {
            return;
        }

        if (await EditGroupAsync("New project", "", GroupIcons.Default, GroupPalette.Default)
            is not (string name, string glyph, string colour))
        {
            return;
        }

        viewModel.CreateGroup(name, glyph, colour);
        UpdateBindings();
    }

    private async void EditGroup_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel is not MainViewModel viewModel
            || (sender as FrameworkElement)?.Tag is not RepositoryGroup group)
        {
            return;
        }

        if (await EditGroupAsync("Edit project", group.Name, group.Glyph, group.ColorKey)
            is not (string name, string glyph, string colour))
        {
            return;
        }

        // Renaming goes through the view model, because membership is keyed by the name.
        if (!string.Equals(name, group.Name, StringComparison.Ordinal))
        {
            viewModel.RenameGroup(group, name);
        }

        // Restyling does too, so the repository column's header picks up the new look.
        viewModel.RestyleGroup(group, glyph, colour);
        UpdateBindings();
    }

    private async void DeleteGroup_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel is not MainViewModel viewModel
            || (sender as FrameworkElement)?.Tag is not RepositoryGroup group)
        {
            return;
        }

        ContentDialog confirm = new()
        {
            Title = $"Delete the {group.Name} project?",
            Content = new TextBlock
            {
                Text = "The repositories stay in the list; they simply stop being grouped. "
                    + "Nothing on disk is touched.",
                TextWrapping = TextWrapping.Wrap
            },
            PrimaryButtonText = "Delete group",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = XamlRoot
        };

        if (await confirm.ShowAsync() == ContentDialogResult.Primary)
        {
            viewModel.DeleteGroup(group);
            UpdateBindings();
        }
    }

    /// <summary>Moves a repository from its context menu, for anyone who would rather not drag.</summary>
    private async void MoveToGroup_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel is not MainViewModel viewModel || EntryOf(sender) is not RecentRepository entry)
        {
            return;
        }

        ComboBox picker = new() { Header = "Project", MinWidth = 280 };
        picker.Items.Add("Not in a project");

        foreach (RepositoryGroup group in viewModel.Groups)
        {
            picker.Items.Add(group.Name);
        }

        picker.SelectedIndex = 0;

        ContentDialog dialog = new()
        {
            Title = $"Move {entry.Name}",
            Content = picker,
            PrimaryButtonText = "Move",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = XamlRoot
        };

        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
        {
            return;
        }

        RepositoryGroup? target = picker.SelectedIndex <= 0
            ? null
            : viewModel.Groups[picker.SelectedIndex - 1];

        viewModel.AssignToGroup(entry.Path, target);
        UpdateBindings();
    }

    /// <summary>
    ///  The name, icon and colour editor, shared by creating and editing.
    /// </summary>
    /// <remarks>
    ///  Icon and colour come from fixed sets rather than free pickers: the point of them is telling
    ///  projects apart at a glance, which needs choices that stay legible on both themes. An arbitrary
    ///  colour picker mostly produces ones that do not.
    /// </remarks>
    private async Task<(string Name, string Glyph, string Colour)?> EditGroupAsync(
        string title,
        string name,
        string glyph,
        string colour)
    {
        TextBox nameBox = new() { Header = "Name", Text = name, MinWidth = 320 };
        ComboBox iconBox = new() { Header = "Icon", MinWidth = 200 };

        foreach ((string itemGlyph, string label) in GroupIcons.All)
        {
            StackPanel row = new() { Orientation = Orientation.Horizontal, Spacing = 10 };
            row.Children.Add(new FontIcon { Glyph = itemGlyph, FontSize = 14 });
            row.Children.Add(new TextBlock { Text = label });
            iconBox.Items.Add(new ComboBoxItem { Content = row, Tag = itemGlyph });
        }

        ComboBox colourBox = new() { Header = "Colour", MinWidth = 200 };

        foreach (string key in GroupPalette.Keys)
        {
            StackPanel row = new() { Orientation = Orientation.Horizontal, Spacing = 10 };

            row.Children.Add(new Border
            {
                Width = 14,
                Height = 14,
                CornerRadius = new CornerRadius(3),
                Background = GroupPalette.Accent(key),
                VerticalAlignment = VerticalAlignment.Center
            });

            row.Children.Add(new TextBlock { Text = key });
            colourBox.Items.Add(new ComboBoxItem { Content = row, Tag = key });
        }

        iconBox.SelectedIndex = Math.Max(0, GroupIcons.All.ToList().FindIndex(item => item.Glyph == glyph));
        colourBox.SelectedIndex = Math.Max(0, GroupPalette.Keys.ToList().FindIndex(key => key == colour));

        StackPanel panel = new() { Spacing = 14, Width = 360 };
        panel.Children.Add(nameBox);
        panel.Children.Add(iconBox);
        panel.Children.Add(colourBox);

        ContentDialog dialog = new()
        {
            Title = title,
            Content = panel,
            PrimaryButtonText = "Save",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = XamlRoot
        };

        if (await dialog.ShowAsync() != ContentDialogResult.Primary || nameBox.Text.Trim().Length == 0)
        {
            return null;
        }

        return (
            nameBox.Text.Trim(),
            (iconBox.SelectedItem as ComboBoxItem)?.Tag as string ?? GroupIcons.Default,
            (colourBox.SelectedItem as ComboBoxItem)?.Tag as string ?? GroupPalette.Default);
    }
}

/// <summary>Something the Home page asks the shell to do, since only the shell owns a window handle.</summary>
public enum HomeAction
{
    OpenRepository,
    Clone,
    Initialise,

    /// <summary>Find repositories on this computer and add them to Home.</summary>
    AddRepositories,

    /// <summary>Run the whole first-run setup again.</summary>
    RunSetup
}
