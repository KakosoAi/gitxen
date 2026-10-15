// SPDX-License-Identifier: GPL-3.0-or-later
using Gitxen.Git;
using Gitxen.GitHub;
using Gitxen.Models;
using Gitxen.Services;
using Gitxen.Theming;
using Gitxen.ViewModels;
using Gitxen.Views;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Input;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.ApplicationModel.DataTransfer;
using Windows.Graphics;
using Windows.Storage;
using Windows.Storage.Pickers;
using WinRT.Interop;

namespace Gitxen;

/// <summary>
///  The shell: a custom title bar, the open repositories as tabs, and the empty state.
/// </summary>
/// <remarks>
///  Everything that acts on a repository now lives in <see cref="Views.RepositoryView"/> and the
///  section pages under it, so this window is left with only what is genuinely window-scoped —
///  opening and closing repositories, the UI mode, the theme, and session persistence.
/// </remarks>
public sealed partial class MainWindow : Window
{
    /// <summary>
    ///  Drives the periodic background fetch.
    /// </summary>
    /// <remarks>
    ///  Owned by the window rather than by each repository so that the interval is applied once and
    ///  every open tab is refreshed on the same tick, instead of each keeping its own timer running.
    /// </remarks>
    private DispatcherQueueTimer? _autoFetchTimer;

    /// <summary>
    ///  Saves the session periodically rather than only on close.
    /// </summary>
    /// <remarks>
    ///  Saving on close alone means a crash, a forced quit or a machine losing power throws away
    ///  everything arranged since launch — the groups, the layout, a half-written commit message.
    ///  A periodic save bounds that loss to the interval instead.
    /// </remarks>
    private DispatcherQueueTimer? _sessionSaveTimer;

    /// <summary>Hides the mode confirmation a moment after it appears.</summary>
    private DispatcherQueueTimer? _modeToastTimer;

    public MainWindow()
    {
        ViewModel = new MainViewModel();
        InitializeComponent();

        Title = "Gitxen";
        SetWindowIcon();

        // Draw into the title bar so the window reads as one surface rather than a WinUI app wearing a
        // system caption. The drag region is the empty strip left of the caption buttons.
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(TitleBarDragRegion);

        Launcher.Source = LaunchItems;
        Launcher.CommitSource = SearchCommitsAsync;

        // The grouped source is a resource, so it cannot be bound with x:Bind from the markup.
        ((CollectionViewSource)RootGrid.Resources["GroupedTabsSource"]).Source = ViewModel.TabGroups;

        // The column's and the strip's highlights are driven from here rather than bound: rebuilding
        // their lists clears their selections, and selecting a tab elsewhere has to be reflected too.
        // While a rebuild runs, its selection-changed noise is ignored; the sync at the end resets it.
        ViewModel.TabGroupsRebuilding += (_, _) => _syncingSidebarSelection = true;
        ViewModel.TabGroupsRebuilt += (_, _) => SyncRepositorySelection();
        ViewModel.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(MainViewModel.SelectedTab))
            {
                SyncRepositorySelection();
            }
        };

        SyncRepositorySelection();

        // Takes over the backdrop, the root element's theme and the system caption buttons. The
        // palette itself is already registered; this is the part that needs a window to exist.
        ThemeService.Attach(this, RootGrid);

        // Ctrl+` toggles the terminal, as it does in the editors people already know. Added in code
        // because the backtick key (VK_OEM_3) has no named VirtualKey member for the markup to use.
        KeyboardAccelerator terminalToggle = new()
        {
            Modifiers = Windows.System.VirtualKeyModifiers.Control,
            Key = (Windows.System.VirtualKey)0xC0
        };
        terminalToggle.Invoked += TerminalAccelerator_Invoked;
        RootGrid.KeyboardAccelerators.Add(terminalToggle);

        Closed += MainWindow_Closed;
    }

    public MainViewModel ViewModel { get; }

    /// <summary>
    ///  Whether this start should open on the launcher. Cleared by the app when it was started with a
    ///  folder to open, which is where that person is going.
    /// </summary>
    public bool OpenOnLauncher { get; set; } = true;

    /// <summary>
    ///  Restores the previous session. Called after the window is shown so the tabs stream in visibly
    ///  rather than delaying first paint.
    /// </summary>
    /// <param name="state">
    ///  The session, already read by <c>App</c> so that the theme could be applied before the window
    ///  was built.
    /// </param>
    public async Task RestoreSessionAsync(SessionState state)
    {
        // Before anything is awaited, so the first frame drawn is the launcher rather than Home
        // filling in behind it.
        // Set up already — unless this is the first start after a fresh install, which gets the wizard
        // again even when the settings survived an earlier uninstall.
        string installToken = InstallInfo.FirstRunToken;
        _setupToken = state.SetupToken ?? "";
        bool setUp = state.HasCompletedSetup && (installToken.Length == 0 || installToken == _setupToken);

        if (OpenOnLauncher && AppOptions.StartWithLauncher && setUp)
        {
            ShowLauncher(asStartScreen: true);
        }

        // Nothing awaits this, so an escaping exception would vanish without trace and simply leave
        // the shell looking like a first run. Record it instead.
        try
        {
            // The column keeps whatever width it was dragged to, and whether it was collapsed.
            if (state.SidebarWidth > 120)
            {
                Sidebar.Width = state.SidebarWidth;
                _expandedSidebarWidth = state.SidebarWidth;
            }

            if (state.SidebarCollapsed)
            {
                SetSidebarCollapsed(true);
            }

            // Before the session's repositories: everything restored after this measures at the
            // zoomed size rather than being laid out twice.
            ApplyZoom(AppOptions.Zoom);
            StatusBarView.ShowZoom(AppOptions.Zoom);

            RestoreWindowBounds(state.Window);

            await ViewModel.RestoreSessionAsync(state);

            StartAutoFetch();
            StartSessionSaves();
            await Home.RefreshAsync();

            // Fills the search index in the background, a repository at a time, once the window is up.
            QueueIndexing();

            _hasCompletedSetup = setUp;

            if (!_hasCompletedSetup)
            {
                ShowSetupWizard();
            }
        }
        catch (Exception ex)
        {
            SessionStore.LogRestoreFailure(ex);
        }
    }

    private void StartAutoFetch()
    {
        _autoFetchTimer?.Stop();

        if (AppOptions.AutoFetchMinutes <= 0)
        {
            return;
        }

        _autoFetchTimer = DispatcherQueue.CreateTimer();
        _autoFetchTimer.Interval = TimeSpan.FromMinutes(AppOptions.AutoFetchMinutes);

        // Not an async lambda: an exception escaping a void-returning delegate would crash the
        // process, so the work goes through a method that handles its own failures.
        _autoFetchTimer.Tick += (_, _) => _ = FetchAllQuietlyAsync();
        _autoFetchTimer.Start();
    }

    /// <summary>
    ///  Puts the application icon on the window itself.
    /// </summary>
    /// <remarks>
    ///  ApplicationIcon only embeds the icon in the executable, which the shell reads for the
    ///  file and the shortcut. The taskbar button and Alt+Tab read the icon the window carries,
    ///  and an unpackaged WinUI window has none until it is given one.
    /// </remarks>
    private void SetWindowIcon()
    {
        try
        {
            string icon = Path.Combine(AppContext.BaseDirectory, "Assets", "gitxen.ico");

            if (File.Exists(icon))
            {
                AppWindow.SetIcon(icon);
            }
        }
        catch (Exception)
        {
            // A missing or unreadable icon is not worth refusing to start over.
        }
    }

    private void StartSessionSaves()
    {
        _sessionSaveTimer?.Stop();
        _sessionSaveTimer = DispatcherQueue.CreateTimer();
        _sessionSaveTimer.Interval = TimeSpan.FromSeconds(30);
        _sessionSaveTimer.Tick += (_, _) => SaveSession();
        _sessionSaveTimer.Start();
    }

    /// <summary>The smallest window worth restoring; anything less was never a size someone chose.</summary>
    private const int MinimumWindowWidth = 800;

    private const int MinimumWindowHeight = 560;

    /// <summary>
    ///  The bounds last seen while the window was in its normal state, which is what is saved.
    /// </summary>
    /// <remarks>
    ///  Read at save time, the window can be minimised, maximised or mid-resize, and a minimised window
    ///  reports a sliver — one save like that brought Gitxen back as a 47-pixel strip with only its
    ///  close button showing. So only a normal, full-sized window updates this, and it is what the
    ///  session keeps.
    /// </remarks>
    private WindowBounds? _normalBounds;

    /// <summary>
    ///  Puts the window where it was, unless that was too small to use or is no longer on any screen —
    ///  a monitor unplugged, a resolution changed — in which case it opens at a sensible size, centred
    ///  on the main screen.
    /// </summary>
    private void RestoreWindowBounds(WindowBounds? saved)
    {
        AppWindow.Changed += (_, args) =>
        {
            if (args.DidSizeChange || args.DidPositionChange || args.DidPresenterChange)
            {
                RememberNormalBounds();
            }
        };

        if (saved is not null
            && saved.Width >= MinimumWindowWidth
            && saved.Height >= MinimumWindowHeight
            && DisplayArea.GetFromRect(new RectInt32(saved.X, saved.Y, saved.Width, saved.Height), DisplayAreaFallback.None) is not null)
        {
            AppWindow.MoveAndResize(new RectInt32(saved.X, saved.Y, saved.Width, saved.Height));
        }
        else
        {
            RectInt32 work = DisplayArea.Primary.WorkArea;
            int width = Math.Min(1440, work.Width - 80);
            int height = Math.Min(920, work.Height - 80);

            AppWindow.MoveAndResize(new RectInt32(
                work.X + ((work.Width - width) / 2),
                work.Y + ((work.Height - height) / 2),
                width,
                height));
        }

        RememberNormalBounds();
    }

    private void RememberNormalBounds()
    {
        bool normal = AppWindow.Presenter is not OverlappedPresenter presenter || presenter.State == OverlappedPresenterState.Restored;

        if (normal && AppWindow.Size.Width >= MinimumWindowWidth && AppWindow.Size.Height >= MinimumWindowHeight)
        {
            _normalBounds = new WindowBounds
            {
                X = AppWindow.Position.X,
                Y = AppWindow.Position.Y,
                Width = AppWindow.Size.Width,
                Height = AppWindow.Size.Height
            };
        }
    }

    /// <summary>Writes the session as it stands, including the window's last normal bounds.</summary>
    private void SaveSession()
    {
        SessionState state = ViewModel.CaptureSession(_normalBounds);

        AppOptions.CopyTo(state);

        // The collapsed rail's 40px must never be recorded as the column's width, or expanding
        // after a restart would give back a sliver.
        state.SidebarWidth = _sidebarCollapsed ? _expandedSidebarWidth : Sidebar.Width;
        state.SidebarCollapsed = _sidebarCollapsed;
        state.HasCompletedSetup = _hasCompletedSetup;
        state.SetupToken = _setupToken;
        SessionStore.Save(state);
    }

    private async Task FetchAllQuietlyAsync()
    {
        foreach (RepositoryTabViewModel tab in ViewModel.Repositories.ToList())
        {
            await tab.AutoFetchAsync();
        }
    }

    private void MainWindow_Closed(object sender, WindowEventArgs args)
    {
        _sessionSaveTimer?.Stop();
        SaveSession();

        // Explicitly, so no shell process outlives the window that opened it.
        Repository.ShutdownTerminals();
    }

    private async void RepositoryTabs_AddTabButtonClick(TabView sender, object args) =>
        await PickAndOpenRepositoryAsync();

    private async void AddRepository_Click(object sender, RoutedEventArgs e) =>
        await PickAndOpenRepositoryAsync();

    /// <summary>
    ///  Runs an action the Home page asked for. These need a window handle for their file pickers,
    ///  which only the window has.
    /// </summary>
    private async void Home_ActionRequested(object? sender, HomeAction action)
    {
        switch (action)
        {
            case HomeAction.OpenRepository:
                await PickAndOpenRepositoryAsync();
                break;

            case HomeAction.Clone:
                await CloneAsync();
                break;

            case HomeAction.Initialise:
                await InitAsync();
                break;

            case HomeAction.RunSetup:
                ShowSetupWizard();
                break;
        }
    }

    private async void Home_OpenRequested(object? sender, string path) => await OpenRecentAsync(path);

    // ---- Repository column -----------------------------------------------------------------------

    /// <summary>The repository being dragged between projects in the column.</summary>
    private SidebarRow? _draggingRow;

    /// <summary>
    ///  Suppresses the selection handler while the selection is being set from the view model, so a
    ///  programmatic sync is not mistaken for a click.
    /// </summary>
    private bool _syncingSidebarSelection;

    private void SelectHome_Click(object sender, RoutedEventArgs e) => ViewModel.SelectedTab = ViewModel.Home;

    /// <summary>
    ///  A click in the column: switch to the repository when it is open, open it when it is not.
    /// </summary>
    /// <remarks>
    ///  Selection is driven from code rather than a two-way binding: the rows are not the tabs, and
    ///  rebuilding the sections clears the list's selection — a binding would write that clearance
    ///  into <see cref="MainViewModel.SelectedTab"/> and bounce the shell back to Home.
    /// </remarks>
    private async void SidebarList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_syncingSidebarSelection && SidebarList.SelectedItem is SidebarRow row)
        {
            await SelectRowAsync(row);
        }
    }

    /// <summary>A click in the strip: Home, an open repository, or one to open. Same as the column.</summary>
    private async void RepositoryTabs_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_syncingSidebarSelection && RepositoryTabs.SelectedItem is SidebarRow row)
        {
            await SelectRowAsync(row);
        }
    }

    private async Task SelectRowAsync(SidebarRow row)
    {
        // A project's label is not somewhere to go: it folds its tabs away or brings them back, and
        // the selection stays where it was (or moves off a tab that has just been folded).
        if (row.IsGroupLabel && row.Group is RepositoryGroup group)
        {
            ViewModel.ToggleStripGroup(group);
            SyncRepositorySelection();
            return;
        }

        if (row.IsHome)
        {
            ViewModel.SelectedTab = ViewModel.Home;
            return;
        }

        if (row.OpenTab is RepositoryTabViewModel tab)
        {
            ViewModel.SelectedTab = tab;
            return;
        }

        await OpenRecentAsync(row.Path);

        // On success the rebuild has already re-selected the new tab's row; on failure this puts the
        // highlight back where it belongs instead of leaving it on a repository that did not open.
        SyncRepositorySelection();
    }

    /// <summary>Points the column's and the strip's highlights at the selected tab's row, if any.</summary>
    private void SyncRepositorySelection()
    {
        _syncingSidebarSelection = true;

        ShellTab? selected = ViewModel.SelectedTab;

        SidebarList.SelectedItem = selected is RepositoryTabViewModel
            ? ViewModel.TabGroups
                .SelectMany(section => section.Items)
                .FirstOrDefault(row => ReferenceEquals(row.OpenTab, selected))
            : null;

        RepositoryTabs.SelectedItem = ViewModel.StripRows.FirstOrDefault(row =>
            selected is HomeTabViewModel ? row.IsHome : ReferenceEquals(row.OpenTab, selected));

        _syncingSidebarSelection = false;
    }

    private void ToggleSidebarGroup_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is ShellTabGroup { Group: RepositoryGroup group })
        {
            ViewModel.ToggleGroupExpanded(group);
        }
    }

    /// <summary>
    ///  A project header: left opens it, right closes it, middle clears it out.
    /// </summary>
    /// <remarks>
    ///  One direction per button rather than a toggle, so each is something you can aim for without
    ///  first checking what state the project is in. Clicks on the chevron button the header hosts do
    ///  not reach here — it handles its own pointer events.
    /// </remarks>
    private async void SidebarGroupHeader_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not ShellTabGroup { Group: RepositoryGroup group })
        {
            return;
        }

        PointerPointProperties properties = e.GetCurrentPoint((UIElement)sender).Properties;

        if (properties.IsMiddleButtonPressed)
        {
            e.Handled = true;
            await RemoveGroupRepositoriesAsync(group);
            return;
        }

        if (properties.IsRightButtonPressed)
        {
            e.Handled = true;
            ViewModel.SetGroupExpanded(group, expanded: false);
            return;
        }

        if (properties.IsLeftButtonPressed)
        {
            ViewModel.SetGroupExpanded(group, expanded: true);
        }
    }

    /// <summary>
    ///  Middle click on a repository row: takes it off the lists and out of its project.
    /// </summary>
    /// <remarks>
    ///  Confirmed first. It is one button press, it cannot be undone from the app, and it closes the
    ///  repository on the way out — too much to happen from a click that is easy to make by accident
    ///  while scrolling.
    /// </remarks>
    private async void SidebarItem_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not SidebarRow { IsHome: false } row
            || !e.GetCurrentPoint((UIElement)sender).Properties.IsMiddleButtonPressed)
        {
            return;
        }

        e.Handled = true;

        if (!await ConfirmAsync(
            "Remove this repository?",
            $"{row.Title} will be closed and taken off the lists. Nothing on disk is touched, and it can be opened again.",
            "Remove"))
        {
            return;
        }

        ViewModel.RemoveRepository(row.Path);
        Home.UpdateBindings();
    }

    /// <summary>Middle click on a project header: takes every repository in it off the lists.</summary>
    private async Task RemoveGroupRepositoriesAsync(RepositoryGroup group)
    {
        IReadOnlyList<string> paths = ViewModel.RepositoriesIn(group);

        if (paths.Count == 0)
        {
            return;
        }

        if (!await ConfirmAsync(
            $"Remove {paths.Count} repositor{(paths.Count == 1 ? "y" : "ies")} from {group.Name}?",
            "They will be closed and taken off the lists. Nothing on disk is touched, and they can be opened again.",
            "Remove all"))
        {
            return;
        }

        foreach (string path in paths)
        {
            ViewModel.RemoveRepository(path);
        }

        Home.UpdateBindings();
    }

    /// <summary>A yes/no dialog, with the destructive answer as the one that has to be chosen.</summary>
    private async Task<bool> ConfirmAsync(string title, string message, string confirmText)
    {
        ContentDialog dialog = new()
        {
            Title = title,
            Content = message,
            PrimaryButtonText = confirmText,
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = RootGrid.XamlRoot
        };

        return await dialog.ShowAsync() == ContentDialogResult.Primary;
    }

    private void SidebarItem_DragStarting(UIElement sender, DragStartingEventArgs args)
    {
        _draggingRow = (sender as FrameworkElement)?.DataContext as SidebarRow;

        // Something has to be offered or the drop targets never light up.
        args.Data.SetText(_draggingRow?.Path ?? "");
        args.Data.RequestedOperation = DataPackageOperation.Move;
    }

    private void SidebarGroup_DragOver(object sender, DragEventArgs e)
    {
        e.AcceptedOperation = _draggingRow is null ? DataPackageOperation.None : DataPackageOperation.Move;
        e.DragUIOverride.Caption = "Move to this project";
        e.DragUIOverride.IsGlyphVisible = false;
    }

    /// <summary>
    ///  Drops a repository onto a project header in the column.
    /// </summary>
    /// <remarks>
    ///  Goes through the same assignment the Home page uses, so a repository moved here is moved
    ///  everywhere — the column, the tab order and the Home page all read the one membership.
    /// </remarks>
    private void SidebarGroup_Drop(object sender, DragEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not ShellTabGroup section
            || _draggingRow is not SidebarRow row)
        {
            return;
        }

        _draggingRow = null;
        ViewModel.AssignToGroup(row.Path, section.Group);
    }

    /// <summary>Closes an open repository's tab from its row in the column, list intact.</summary>
    private void CloseRepository_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is SidebarRow { OpenTab: RepositoryTabViewModel tab })
        {
            ViewModel.CloseTab(tab);
        }
    }

    private void RemoveRepository_Click(object sender, RoutedEventArgs e)
    {
        // FrameworkElement rather than Button: this also arrives from the row's context menu item.
        if ((sender as FrameworkElement)?.Tag is SidebarRow { IsHome: false } row)
        {
            ViewModel.RemoveRepository(row.Path);

            // Home's captions and empty-state text are x:Bind projections that only move when told.
            Home.UpdateBindings();
        }
    }

    // ---- Sidebar collapse ------------------------------------------------------------------------

    /// <summary>The width to give back on expand; the collapsed rail overwrites Sidebar.Width.</summary>
    private double _expandedSidebarWidth = 280;

    private bool _sidebarCollapsed;

    // ---- Zoom -----------------------------------------------------------------------------------

    /// <summary>
    ///  Scales the window's contents, the way a browser's zoom does.
    /// </summary>
    /// <remarks>
    ///  <para>
    ///   A render transform on its own would scale what is drawn and leave the layout at its old
    ///   size: the content would be painted past the window's edges while still believing it fits.
    ///   So the scaled element is given an explicit size — the host's, divided by the scale — and
    ///   then scaled back up by the same factor. The layout then happens at the zoomed size and the
    ///   result fills the window exactly.
    ///  </para>
    ///  <para>
    ///   Dialogs are unaffected. A ContentDialog is shown in the XamlRoot's own popup layer, which is
    ///   outside this tree, so it keeps the system's size — which is the right thing for something
    ///   that has to stay readable regardless.
    ///  </para>
    /// </remarks>
    private void ApplyZoom(double scale)
    {
        AppOptions.Zoom = Math.Clamp(scale, AppOptions.MinimumZoom, AppOptions.MaximumZoom);

        ZoomTransform.ScaleX = AppOptions.Zoom;
        ZoomTransform.ScaleY = AppOptions.Zoom;

        SizeRootToHost();
    }

    private void SizeRootToHost()
    {
        if (ZoomHost.ActualWidth <= 0 || ZoomHost.ActualHeight <= 0)
        {
            return;
        }

        RootGrid.Width = ZoomHost.ActualWidth / AppOptions.Zoom;
        RootGrid.Height = ZoomHost.ActualHeight / AppOptions.Zoom;
    }

    private void ZoomHost_SizeChanged(object sender, SizeChangedEventArgs e) => SizeRootToHost();

    private void StatusBar_ZoomRequested(object? sender, double scale) => ApplyZoom(scale);

    private void CollapseSidebar_Click(object sender, RoutedEventArgs e) => SetSidebarCollapsed(true);

    private void ExpandSidebar_Click(object sender, RoutedEventArgs e) => SetSidebarCollapsed(false);

    /// <summary>
    ///  The column's header: left click opens it, right click closes it to the rail.
    /// </summary>
    /// <remarks>
    ///  The same convention as the project headers inside the column, so the two do not disagree
    ///  about what a mouse button means. The buttons the header hosts handle their own pointer
    ///  events and never reach here.
    /// </remarks>
    private void SidebarHeader_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        PointerPointProperties properties = e.GetCurrentPoint((UIElement)sender).Properties;

        if (properties.IsRightButtonPressed)
        {
            e.Handled = true;
            SetSidebarCollapsed(true);
        }
        else if (properties.IsLeftButtonPressed)
        {
            SetSidebarCollapsed(false);
        }
    }

    /// <summary>The collapsed rail: a click anywhere on it, not only on its chevron, brings it back.</summary>
    private void SidebarRail_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (e.GetCurrentPoint((UIElement)sender).Properties.IsLeftButtonPressed)
        {
            SetSidebarCollapsed(false);
        }
    }

    private void SetSidebarCollapsed(bool collapsed)
    {
        if (collapsed && !_sidebarCollapsed)
        {
            _expandedSidebarWidth = Sidebar.Width;
        }

        _sidebarCollapsed = collapsed;
        SidebarExpanded.Visibility = collapsed ? Visibility.Collapsed : Visibility.Visible;
        SidebarRail.Visibility = collapsed ? Visibility.Visible : Visibility.Collapsed;
        Sidebar.Width = collapsed ? 40 : _expandedSidebarWidth;
    }

    // ---- Git activity console --------------------------------------------------------------------

    private void StatusBar_ConsoleRequested(object? sender, EventArgs e) => GitConsole.Toggle();

    private void ConsoleAccelerator_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        args.Handled = true;
        GitConsole.Toggle();
    }

    /// <summary>
    ///  Clones a repository into a folder the user picks, then opens it.
    /// </summary>
    /// <remarks>
    ///  The folder picker chooses the <em>parent</em>: git creates the repository directory itself, and
    ///  cloning into an existing non-empty folder fails, so asking for the destination directly would
    ///  only invite that error.
    /// </remarks>
    private async Task CloneAsync()
    {
        TextBox url = new() { Header = "Repository URL", PlaceholderText = "https://github.com/owner/repo.git" };
        TextBox folder = new() { Header = "Folder name (optional)", PlaceholderText = "taken from the URL" };
        CheckBox submodules = new() { Content = "Also clone submodules", IsChecked = true };

        StackPanel panel = new() { Spacing = 12, Width = 460 };
        panel.Children.Add(url);
        panel.Children.Add(folder);
        panel.Children.Add(submodules);

        ContentDialog dialog = new()
        {
            Title = "Clone repository",
            Content = panel,
            PrimaryButtonText = "Choose folder…",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = RootGrid.XamlRoot
        };

        if (await dialog.ShowAsync() != ContentDialogResult.Primary || string.IsNullOrWhiteSpace(url.Text))
        {
            return;
        }

        if (await PickFolderAsync() is not string parent)
        {
            return;
        }

        // While git works: what it is doing, how far, and Cancel, which stops it and removes the
        // half-cloned folder.
        using CancellationTokenSource cancel = new();
        TextBlock phase = new() { Text = "Starting…", TextTrimming = TextTrimming.CharacterEllipsis };
        ProgressBar bar = new() { IsIndeterminate = true, Maximum = 100 };
        TextBlock detail = new() { Opacity = 0.7, FontSize = 12, TextTrimming = TextTrimming.CharacterEllipsis };
        StackPanel progressPanel = new() { Spacing = 8, Width = 440 };
        progressPanel.Children.Add(phase);
        progressPanel.Children.Add(bar);
        progressPanel.Children.Add(detail);

        ContentDialog progressDialog = new()
        {
            Title = $"Cloning {url.Text.Trim()}",
            Content = progressPanel,
            CloseButtonText = "Cancel",
            XamlRoot = RootGrid.XamlRoot
        };

        bool finished = false;
        progressDialog.Closing += (_, _) =>
        {
            if (!finished)
            {
                cancel.Cancel();
            }
        };

        ProgressPump pump = new(DispatcherQueue, progress =>
        {
            phase.Text = ProgressPump.Phase(progress);
            detail.Text = ProgressPump.Detail(progress);
            bar.IsIndeterminate = progress.Percent is null;
            bar.Value = progress.Percent ?? 0;
        });

        Task<ContentDialogResult> shown = progressDialog.ShowAsync().AsTask();
        GitOperationResult result;

        try
        {
            result = await ViewModel.CloneAsync(
                url.Text.Trim(), parent, folder.Text.Trim(), submodules.IsChecked == true, pump.Report, cancel.Token);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        finally
        {
            finished = true;
            progressDialog.Hide();
            await shown;
        }

        if (result.Succeeded)
        {
            await Home.RefreshAsync();
        }
        else
        {
            await ShowMessageAsync("Clone failed", result.Output);
        }
    }

    /// <summary>
    ///  New repository: the dialog, then the template files fetched from GitHub, the repository made
    ///  and opened, and — when asked — created on GitHub and pushed.
    /// </summary>
    private async Task InitAsync()
    {
        string defaultParent = ViewModel.RecentRepositories.FirstOrDefault() is { } recent && Path.GetDirectoryName(recent.Path) is string above
            ? above
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "source", "repos");

        if (await NewRepositoryDialog.ShowAsync(RootGrid.XamlRoot, defaultParent, PickFolderAsync) is not NewRepositoryChoices choices)
        {
            return;
        }

        GitHubClient gh = new(GitHubService.CreateCli(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)));
        RepositoryTemplate template = await RepositoryStarter.PrepareAsync(choices, gh, ViewModel.GlobalGit.Get("user.name"));

        GitOperationResult result = await ViewModel.CreateRepositoryAsync(template);
        await Home.RefreshAsync();

        if (!result.Succeeded)
        {
            await ShowMessageAsync("Could not create the repository", result.Output);
            return;
        }

        if (choices.Publish && await RepositoryStarter.PublishAsync(choices, template, gh, GitActivity.Record) is { Succeeded: false } published)
        {
            await ShowMessageAsync("Created here, but not on GitHub", published.AllOutput.Trim());
        }
    }

    private async Task OpenRecentAsync(string path)
    {
        try
        {
            await OpenRepositoryPathAsync(path);
        }
        catch (Exception ex)
        {
            await ShowMessageAsync("Could not open repository", ex.Message);
        }
    }

    private void RootGrid_DragOver(object sender, DragEventArgs e)
    {
        if (e.DataView.Contains(StandardDataFormats.StorageItems))
        {
            e.AcceptedOperation = DataPackageOperation.Link;
            e.DragUIOverride.Caption = "Open repository";
        }
    }

    private async void RootGrid_Drop(object sender, DragEventArgs e)
    {
        if (!e.DataView.Contains(StandardDataFormats.StorageItems))
        {
            return;
        }

        IReadOnlyList<IStorageItem> items = await e.DataView.GetStorageItemsAsync();

        foreach (IStorageItem item in items.OfType<StorageFolder>())
        {
            await OpenRepositoryPathAsync(item.Path);
        }
    }

    private void RepositoryTabs_TabCloseRequested(TabView sender, TabViewTabCloseRequestedEventArgs args)
    {
        if (args.Item is SidebarRow { OpenTab: RepositoryTabViewModel tab })
        {
            ViewModel.CloseTab(tab);
        }
    }

    private void CloseTabAccelerator_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        args.Handled = true;

        if (ViewModel.SelectedTab is RepositoryTabViewModel tab)
        {
            ViewModel.CloseTab(tab);
        }
    }

    private async void OpenRepositoryAccelerator_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        args.Handled = true;
        await PickAndOpenRepositoryAsync();
    }

    private async void RefreshAccelerator_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        args.Handled = true;

        if (ViewModel.SelectedTab is RepositoryTabViewModel tab)
        {
            await tab.LoadAsync();
        }
    }

    /// <summary>
    ///  Opens the command palette. Window-level so it works from any page, which is the whole point of
    ///  a palette.
    /// </summary>
    private async void PaletteAccelerator_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        args.Handled = true;

        // The palette acts on a repository, so it has nothing to offer while Home is selected.
        if (ViewModel.SelectedRepository is not null && !IsDialogOpen())
        {
            await Repository.ShowPaletteAsync();
        }
    }

    /// <summary>
    ///  Ctrl+T: go to anything. In a repository that is its branches, tags, commits, stashes, files and
    ///  pages, and the other repositories; on Home, which is where the app opens, the repositories.
    /// </summary>
    /// <remarks>
    ///  It used to do nothing on Home, so the shortcut seemed broken until a repository had been opened
    ///  by hand — and pressing it again while the palette was up opened a second dialog, which WinUI
    ///  answers by ending the process.
    /// </remarks>
    private async void GoToAccelerator_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        args.Handled = true;

        if (IsDialogOpen())
        {
            return;
        }

        List<GoToItem> repositories = RepositoryGoToItems();

        if (ViewModel.SelectedRepository is not null)
        {
            await Repository.ShowGoToAsync(repositories);
            return;
        }

        GoToPalette palette = new(repositories, "A repository — its name or part of its folder") { XamlRoot = RootGrid.XamlRoot };
        await palette.ShowAsync();

        if (palette.Chosen is GoToItem chosen)
        {
            await chosen.Invoke();
        }
    }

    // ---- The quick launcher ---------------------------------------------------------------------

    private void LauncherAccelerator_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        args.Handled = true;

        if (Launcher.IsShowing)
        {
            HideLauncher();
        }
        else if (!IsDialogOpen() && SetupWizard.Visibility != Visibility.Visible)
        {
            ShowLauncher(asStartScreen: false);
        }
    }

    private void ShowLauncher(bool asStartScreen)
    {
        // What is open is checked again whenever the launcher is called up: two git reads each when
        // nothing has moved, and the commits just made are then in the search.
        SearchIndexer.Enqueue(ViewModel.Repositories.Select(tab => tab.WorkingDir));
        Launcher.Show(asStartScreen);

        // The launcher covers the title bar, so it lends the window a strip of its own to drag by.
        SetTitleBar(Launcher.DragStrip);
    }

    private void HideLauncher()
    {
        if (!Launcher.IsShowing)
        {
            return;
        }

        Launcher.Hide();
        SetTitleBar(TitleBarDragRegion);
    }

    private void Launcher_Dismissed(object? sender, EventArgs e) => HideLauncher();

    /// <summary>Every repository Gitxen knows is brought into the search index, open ones first.</summary>
    private void QueueIndexing() =>
        SearchIndexer.Enqueue(SearchIndexer.KnownPaths(
            ViewModel.Repositories.Select(tab => tab.WorkingDir),
            ViewModel.RecentRepositories,
            ViewModel.Recent));

    /// <summary>The launcher's commit results, as launcher items.</summary>
    private static async Task<IReadOnlyList<LaunchItem>> SearchCommitsAsync(string query, CancellationToken cancellationToken)
    {
        IReadOnlyList<IndexedCommit> found = await SearchIndexer.SearchAsync(query, limit: 6, cancellationToken);

        return
        [
            .. found.Select(commit => new LaunchItem(
                commit.Subject,
                commit.RepositoryPath,
                $"{commit.ShortId} · {commit.RepositoryName} · {TimeText.Relative(commit.Date)}",
                isOpen: false,
                commit.Hash))
        ];
    }

    /// <summary>
    ///  Opens the repository a commit was found in, on History, with that commit selected. A commit
    ///  older than the page History has loaded is reached by walking history from it.
    /// </summary>
    private async void Launcher_CommitChosen(object? sender, (string Folder, string CommitId) chosen)
    {
        HideLauncher();
        ViewModel.ExitZen();

        if (LaunchRequest.FindRepositoryRoot(chosen.Folder) is not string root)
        {
            await ShowMessageAsync("Repository not found", $"{chosen.Folder} is not a repository any more.");
            return;
        }

        try
        {
            Task opening = ViewModel.OpenRepositoryAsync(root);
            await Repository.NavigateToSectionAsync("history");
            await opening;

            if (ViewModel.SelectedRepository is RepositoryTabViewModel tab)
            {
                await RevealCommitAsync(tab, chosen.CommitId);
            }

            await Home.RefreshAsync();
        }
        catch (Exception ex)
        {
            await ShowMessageAsync("Could not open the commit", ex.Message);
        }
    }

    private static async Task RevealCommitAsync(RepositoryTabViewModel tab, string commitId)
    {
        bool Loaded() => tab.Commits.Any(commit => !commit.IsWorkingDirectory && commit.FullHash == commitId);

        if (!Loaded())
        {
            // Older than the page loaded so far: walk history from that commit instead, and wait for
            // the first rows to arrive.
            tab.Query = tab.Query with { Scope = RevisionScope.CurrentBranch, FromRevision = commitId };

            for (int attempt = 0; attempt < 80 && !Loaded(); attempt++)
            {
                await Task.Delay(100);
            }
        }

        if (Loaded())
        {
            tab.RevealCommit(commitId, null);
        }
    }

    /// <summary>
    ///  Opens the chosen repository — switching to it if it is already open — on its History page.
    /// </summary>
    private async void Launcher_Chosen(object? sender, string folder)
    {
        HideLauncher();
        ViewModel.ExitZen();

        if (LaunchRequest.FindRepositoryRoot(folder) is not string root)
        {
            // A pasted folder that is not a repository: the same offer Explorer's entry makes.
            await OpenRequestedAsync(folder);
            return;
        }

        try
        {
            // Not awaited straight away: the tab is selected before the history starts loading, and
            // the page is switched then, so History is what fills in rather than the last section.
            Task opening = ViewModel.OpenRepositoryAsync(root);
            await Repository.NavigateToSectionAsync("history");
            await opening;
            await Home.RefreshAsync();
        }
        catch (Exception ex)
        {
            await ShowMessageAsync("Could not open repository", ex.Message);
        }
    }

    /// <summary>
    ///  What the launcher offers: the open repositories, then every other one Gitxen knows, most
    ///  recently used first.
    /// </summary>
    private IReadOnlyList<LaunchItem> LaunchItems()
    {
        List<LaunchItem> items = [];
        HashSet<string> seen = new(StringComparer.OrdinalIgnoreCase);

        foreach (RepositoryTabViewModel tab in ViewModel.Repositories)
        {
            seen.Add(tab.WorkingDir);
            items.Add(new LaunchItem(tab.Title, tab.WorkingDir, tab.Branch.Length > 0 ? $"open · {tab.Branch}" : "open", isOpen: true));
        }

        foreach (RecentRepository entry in ViewModel.RecentRepositories)
        {
            if (entry.IsMissing || !seen.Add(entry.Path))
            {
                continue;
            }

            string group = ViewModel.Groups.FirstOrDefault(candidate => candidate.Repositories.Contains(entry))?.Name ?? "";
            string detail = string.Join(" · ", new[] { group, entry.Branch }.Where(part => part.Length > 0));
            items.Add(new LaunchItem(entry.Name, entry.Path, detail, isOpen: false));
        }

        // Before Home has been built the entries are not there yet, but the paths are.
        foreach (string path in ViewModel.Recent)
        {
            if (seen.Add(path))
            {
                items.Add(new LaunchItem(Path.GetFileName(path.TrimEnd('\\', '/')), path, "", isOpen: false));
            }
        }

        return items;
    }

    /// <summary>Every repository Gitxen knows: the open ones switch tabs, the rest open.</summary>
    private List<GoToItem> RepositoryGoToItems()
    {
        List<GoToItem> items = [];
        HashSet<string> open = new(StringComparer.OrdinalIgnoreCase);

        foreach (RepositoryTabViewModel tab in ViewModel.Repositories)
        {
            RepositoryTabViewModel target = tab;
            open.Add(tab.WorkingDir);

            items.Add(new GoToItem("\uE8B7", tab.Title, "Repository", tab.WorkingDir, () =>
            {
                ViewModel.SelectedTab = target;
                return Task.CompletedTask;
            }) { CategoryRank = 5 });
        }

        foreach (string path in ViewModel.Recent.Where(path => !open.Contains(path)))
        {
            string target = path;
            items.Add(new GoToItem("\uE8B7", Path.GetFileName(path.TrimEnd('\\', '/')), "Repository", path, () => OpenRecentAsync(target))
            {
                CategoryRank = 6
            });
        }

        return items;
    }

    /// <summary>
    ///  Whether a dialog is already showing. WinUI allows one at a time and ends the process when a
    ///  second is opened, so a shortcut that opens one must check first.
    /// </summary>
    private bool IsDialogOpen() =>
        RootGrid.XamlRoot is XamlRoot root
        && VisualTreeHelper.GetOpenPopupsForXamlRoot(root).Any(popup => popup.Child is ContentDialog);

    /// <summary>
    ///  Lists the keyboard shortcuts.
    /// </summary>
    /// <remarks>
    ///  Shortcuts that cannot be discovered are shortcuts nobody uses. F1 rather than the more
    ///  fashionable Ctrl+/ because the latter is not a stable key across keyboard layouts.
    /// </remarks>
    private async void ShortcutsAccelerator_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        args.Handled = true;

        if (IsDialogOpen())
        {
            return;
        }

        (string Keys, string Action)[] shortcuts =
        [
            ("Ctrl+O", "Open a repository"),
            ("Ctrl+W / Ctrl+F4", "Close the current repository"),
            ("Ctrl+R", "Reload the current repository"),
            ("Ctrl+Shift+P", "Command palette"),
            ("Ctrl+K", "Quick launcher: jump to a repository"),
            ("Ctrl+T", "Go to anything: repository, branch, tag, commit, stash, file"),
            ("Ctrl+Enter", "Commit (on the Changes page)"),
            ("Ctrl+F", "Filter commits (on the History page)"),
            ("Ctrl+`", "Toggle the terminal"),
            ("F12", "Git activity console"),
            ("F1", "This list"),
            ("Ctrl+Shift+1", "Simple mode"),
            ("Ctrl+Shift+2", "Advanced mode: reflog, submodules, worktrees, amend"),
            ("Ctrl+Shift+Z", "Zen mode, on and off"),
            ("Escape", "Leave Zen mode")
        ];

        StackPanel panel = new() { Spacing = 8, Width = 420 };

        foreach ((string keys, string action) in shortcuts)
        {
            Grid row = new() { ColumnSpacing = 16 };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(150) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(250) });

            TextBlock keyText = new()
            {
                Text = keys,
                FontFamily = new FontFamily("Cascadia Mono, Consolas"),
                FontSize = 12
            };

            TextBlock actionText = new() { Text = action };
            Grid.SetColumn(actionText, 1);

            row.Children.Add(keyText);
            row.Children.Add(actionText);
            panel.Children.Add(row);
        }

        ContentDialog dialog = new()
        {
            Title = "Keyboard shortcuts",
            Content = panel,
            CloseButtonText = "Close",
            XamlRoot = RootGrid.XamlRoot
        };

        await dialog.ShowAsync();
    }

    /// <summary>A status bar segment asking for its section — branch to Branches, changes to Changes.</summary>
    private async void StatusBar_SectionRequested(object? sender, string tag) =>
        await Repository.NavigateToSectionAsync(tag);

    private void ZenExit_Click(object sender, RoutedEventArgs e) => ViewModel.ExitZen();

    private void SimpleModeAccelerator_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        args.Handled = true;
        SwitchMode(UiMode.Simple, "Simple mode");
    }

    private void AdvancedModeAccelerator_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        args.Handled = true;
        SwitchMode(UiMode.Advanced, "Advanced mode");
    }

    /// <summary>Leaves Zen first, if it is on, so the chrome it hid comes back with the new mode.</summary>
    private void SwitchMode(UiMode mode, string name)
    {
        ViewModel.ExitZen();
        ViewModel.Mode = mode;
        ShowModeToast($"{name}  ·  Ctrl+Shift+1 Simple  ·  Ctrl+Shift+2 Advanced  ·  Ctrl+Shift+Z Zen");
    }

    private void ShowModeToast(string text)
    {
        ModeToastText.Text = text;
        ModeToast.Visibility = Visibility.Visible;

        if (_modeToastTimer is null)
        {
            _modeToastTimer = DispatcherQueue.CreateTimer();
            _modeToastTimer.Interval = TimeSpan.FromSeconds(2);
            _modeToastTimer.IsRepeating = false;
            _modeToastTimer.Tick += (_, _) => ModeToast.Visibility = Visibility.Collapsed;
        }

        // Restarted, so pressing a shortcut twice keeps the note up rather than cutting it short.
        _modeToastTimer.Stop();
        _modeToastTimer.Start();
    }

    /// <summary>Ctrl+`: the terminal, which lives in the repository view and needs one open.</summary>
    private void TerminalAccelerator_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        args.Handled = true;

        if (ViewModel.SelectedRepository is not null)
        {
            Repository.ToggleTerminal();
        }
    }

    private void ZenAccelerator_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        args.Handled = true;
        ViewModel.ToggleZen();
    }

    private void EscapeAccelerator_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        if (Launcher.IsShowing)
        {
            args.Handled = true;
            HideLauncher();
            return;
        }

        // Only meaningful in Zen; leave Escape alone otherwise so it keeps its normal behaviour.
        args.Handled = ViewModel.IsZen;
        ViewModel.ExitZen();
    }

    // ---- Setup wizard ----------------------------------------------------------------------------

    /// <summary>
    ///  Whether the wizard has been through, either finished or skipped.
    /// </summary>
    /// <remarks>
    ///  Held here rather than in the view model because it is a property of the installation rather
    ///  than of anything the view model owns, and this is the only place that reads or writes it.
    /// </remarks>
    private bool _hasCompletedSetup;

    /// <summary>The installation the wizard has been shown for, kept in the session.</summary>
    private string _setupToken = "";

    private void ShowSetupWizard()
    {
        // The folder picker has to be initialised with a window handle, which a UserControl has no
        // way to get, so the wizard is handed the window's own picker.
        SetupWizard.Initialize(ViewModel.GlobalGit, ViewModel.Mode, PickFolderAsync);
        SetupWizard.Visibility = Visibility.Visible;
    }

    private void SetupWizard_ThemeChanged(object? sender, EventArgs e) => ThemeService.Apply();

    private async void SetupWizard_Completed(object? sender, SetupOutcome outcome)
    {
        SetupWizard.Visibility = Visibility.Collapsed;

        // Set before anything can fail, so a wizard that has been answered is never shown twice —
        // for this installation. A fresh install brings it back once.
        _hasCompletedSetup = true;
        _setupToken = InstallInfo.FirstRunToken;

        ViewModel.ImportRepositories(
            [.. outcome.Repositories.Select(import => (import.Path, import.ProjectName))],
            [.. outcome.Projects.Select(project => (project.Name.Trim(), project.ColorKey))]);

        // Set through the view model rather than AppOptions: these two drive bindings, and the
        // property setters are what raise the notifications that redraw the strip and the column.
        ViewModel.Layout = outcome.Layout;
        ViewModel.Mode = outcome.Mode;

        // The timer captured the old interval, so a changed one only takes effect once restarted.
        StartAutoFetch();

        await Home.RefreshAsync();

        // Written straight away: everything the wizard collected would otherwise be lost to a crash
        // before the next periodic save.
        SaveSession();
    }

    private async Task PickAndOpenRepositoryAsync()
    {
        if (await PickFolderAsync() is string path)
        {
            await OpenRepositoryPathAsync(path);
        }
    }

    /// <summary>Shows the folder picker. Returns null when the user cancels.</summary>
    private async Task<string?> PickFolderAsync()
    {
        FolderPicker picker = new();

        // Required even when picking folders — PickSingleFolderAsync throws without at least one filter.
        picker.FileTypeFilter.Add("*");
        InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(this));

        StorageFolder? folder = await picker.PickSingleFolderAsync();
        return folder?.Path;
    }

    /// <summary>
    ///  Opens the repository a folder belongs to — from Explorer's "Open with Gitxen", or from
    ///  <c>gitxen &lt;folder&gt;</c> — and offers to create one when it belongs to none.
    /// </summary>
    public async Task OpenRequestedAsync(string folder)
    {
        try
        {
            HideLauncher();
            BringToFront();

            if (LaunchRequest.FindRepositoryRoot(folder) is string root)
            {
                await OpenRepositoryPathAsync(root);
                return;
            }

            if (!Directory.Exists(folder))
            {
                await ShowMessageAsync("Folder not found", $"{folder} does not exist.");
                return;
            }

            ContentDialog ask = new()
            {
                Title = "Not a git repository",
                Content = new TextBlock
                {
                    Text = $"{folder} is not inside a git repository. Create one here?",
                    TextWrapping = TextWrapping.Wrap
                },
                PrimaryButtonText = "Create repository",
                CloseButtonText = "Cancel",
                DefaultButton = ContentDialogButton.Close,
                XamlRoot = RootGrid.XamlRoot
            };

            if (await ask.ShowAsync() != ContentDialogResult.Primary)
            {
                return;
            }

            GitOperationResult result = await ViewModel.InitAsync(folder, bare: false);

            if (result.Succeeded)
            {
                await Home.RefreshAsync();
            }
            else
            {
                await ShowMessageAsync("Could not initialise", result.Output);
            }
        }
        catch (Exception ex)
        {
            // A dialog already open (the setup wizard's, say) makes ShowAsync throw; nothing here is
            // worth crashing over.
            SessionStore.LogCrash(ex);
        }
    }

    /// <summary>Restores the window if it was minimised and brings it in front of whatever was in use.</summary>
    public void BringToFront()
    {
        if (AppWindow.Presenter is OverlappedPresenter { State: OverlappedPresenterState.Minimized } presenter)
        {
            presenter.Restore();
        }

        Activate();
        _ = SetForegroundWindow(WindowNative.GetWindowHandle(this));
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(nint window);

    private async Task OpenRepositoryPathAsync(string path)
    {
        if (!ViewModel.IsValidRepository(path))
        {
            await ShowMessageAsync("Not a git repository", $"{path} is not a git working directory.");
            return;
        }

        await ViewModel.OpenRepositoryAsync(path);
        await Home.RefreshAsync();
    }

    /// <summary>
    ///  Window-scoped failures only — anything a repository reports goes to that repository's InfoBar
    ///  instead, where it does not have to be dismissed before the next action.
    /// </summary>
    private async Task ShowMessageAsync(string title, string message)
    {
        ContentDialog dialog = new()
        {
            Title = title,
            Content = new TextBlock
            {
                Text = message,
                TextWrapping = TextWrapping.Wrap,
                IsTextSelectionEnabled = true
            },
            CloseButtonText = "OK",
            XamlRoot = RootGrid.XamlRoot
        };

        await dialog.ShowAsync();
    }
}
