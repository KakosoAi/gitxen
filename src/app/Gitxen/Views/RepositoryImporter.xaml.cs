// SPDX-License-Identifier: GPL-3.0-or-later
using System.Collections.ObjectModel;
using Gitxen.Models;
using Gitxen.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;

namespace Gitxen.Views;

/// <summary>One repository being added to Home, and the project it goes into.</summary>
/// <param name="Path">The working directory.</param>
/// <param name="ProjectName">The project name, or empty for no project.</param>
public sealed record SetupImport(string Path, string ProjectName);

/// <summary>What <see cref="RepositoryImporter"/> was asked to add: repositories, and projects to create.</summary>
public sealed record ImportPlan(IReadOnlyList<SetupImport> Repositories, IReadOnlyList<ProposedProject> Projects);

/// <summary>
///  Finds the repositories already on this computer and lets the user choose which go on Home, and
///  into which project.
/// </summary>
/// <remarks>
///  <para>
///   Shared by the first-run wizard and Home's "Add repositories", so that adding repositories later
///   does not mean answering the whole setup again.
///  </para>
///  <para>
///   Folders are chips: the common places code is kept are suggested, and any other folder can be
///   added. A chip that is on is scanned once and its repositories listed; turning it off hides them
///   without forgetting them. Results appear as they are found. Repositories already on Home are
///   listed but cannot be picked, so a rescan never adds a second copy.
///  </para>
/// </remarks>
public sealed partial class RepositoryImporter : UserControl
{
    private const string NoProject = "\u0001none";
    private const string FolderProjects = "\u0001folders";
    private const string NewProject = "\u0001new";

    /// <summary>Every repository found, by path, across all the folders scanned.</summary>
    private readonly Dictionary<string, DiscoveredRepository> _found = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The rows shown: the found repositories of the folders that are on, after the filter.</summary>
    private readonly ObservableCollection<DiscoveredRepository> _shown = [];

    private readonly List<Root> _roots = [];
    private readonly Queue<Root> _pending = new();
    private readonly HashSet<string> _known = new(StringComparer.OrdinalIgnoreCase);

    private Func<Task<string?>>? _pickFolder;
    private CancellationTokenSource? _cancel;
    private bool _scanning;

    public RepositoryImporter()
    {
        InitializeComponent();
        RepositoryList.ItemsSource = _shown;
        DepthBox.SelectedIndex = 1;
        UpdateCount();
    }

    /// <summary>The number of repositories that would be added. Raised whenever it may have changed.</summary>
    public event EventHandler? SelectionChanged;

    public int SelectedCount => Selected().Count;

    /// <summary>
    ///  Supplies what the control cannot reach on its own.
    /// </summary>
    /// <param name="pickFolder">The window's folder picker; it needs a window handle a control lacks.</param>
    /// <param name="knownPaths">Repositories already on Home, which are listed but not added again.</param>
    /// <param name="projectNames">Existing projects, offered as places to put what is added.</param>
    internal void Initialize(Func<Task<string?>> pickFolder, IEnumerable<string> knownPaths, IEnumerable<string> projectNames)
    {
        _pickFolder = pickFolder;

        _known.Clear();
        foreach (string path in knownPaths)
        {
            _known.Add(Normalise(path));
        }

        ProjectBox.Items.Clear();
        ProjectBox.Items.Add(new ComboBoxItem { Content = "Home, without a project", Tag = NoProject });
        ProjectBox.Items.Add(new ComboBoxItem { Content = "A project for each folder", Tag = FolderProjects });

        foreach (string name in projectNames.Where(name => name.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            ProjectBox.Items.Add(new ComboBoxItem { Content = $"Project: {name}", Tag = name });
        }

        ProjectBox.Items.Add(new ComboBoxItem { Content = "A new project…", Tag = NewProject });
        ProjectBox.SelectedIndex = 0;

        // Anything already found may now be on Home, so refresh what can be picked.
        foreach (DiscoveredRepository repository in _found.Values)
        {
            repository.IsAlreadyAdded = _known.Contains(Normalise(repository.Path));
        }

        Refilter();
    }

    /// <summary>Offers the usual code folders on this machine and scans them, unless that has been done.</summary>
    public void ScanSuggested()
    {
        foreach (string folder in RepositoryScanner.SuggestedRoots())
        {
            AddRoot(folder, on: true);
        }

        if (_roots.Count == 0)
        {
            StatusText.Text = "No usual code folder was found here. Add the folder you keep your work in.";
        }
    }

    /// <summary>Stops any scan in progress. Called when the host closes.</summary>
    public void CancelScans()
    {
        _pending.Clear();
        _cancel?.Cancel();
    }

    /// <summary>The repositories chosen, and the projects they need, ready for the shell to add.</summary>
    public ImportPlan BuildPlan()
    {
        List<DiscoveredRepository> selected = Selected();
        string choice = (ProjectBox.SelectedItem as ComboBoxItem)?.Tag as string ?? NoProject;
        IReadOnlyList<string> palette = GroupPalette.Keys;

        if (choice == FolderProjects)
        {
            List<string> folders =
            [
                .. selected.Select(repository => repository.SuggestedProject)
                    .Where(name => name.Length > 0)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
            ];

            return new ImportPlan(
                [.. selected.Select(repository => new SetupImport(repository.Path, repository.SuggestedProject))],
                [.. folders.Select((name, index) => new ProposedProject(
                    name,
                    palette[index % palette.Count],
                    selected.Count(repository => string.Equals(repository.SuggestedProject, name, StringComparison.OrdinalIgnoreCase))))]);
        }

        string project = choice switch
        {
            NoProject => "",
            NewProject => NewProjectBox.Text.Trim(),
            _ => choice
        };

        return new ImportPlan(
            [.. selected.Select(repository => new SetupImport(repository.Path, project))],
            project.Length == 0 ? [] : [new ProposedProject(project, GroupPalette.Default, selected.Count)]);
    }

    /// <summary>A sentence for a summary: how many repositories and where they go.</summary>
    public string Describe()
    {
        ImportPlan plan = BuildPlan();
        int count = plan.Repositories.Count;

        string what = count switch
        {
            0 => "No repositories added",
            1 => "1 repository added to Home",
            _ => $"{count} repositories added to Home"
        };

        return plan.Projects.Count switch
        {
            0 => what,
            1 => $"{what}, in {plan.Projects[0].Name}",
            _ => $"{what}, in {plan.Projects.Count} projects"
        };
    }

    // ---- Folders ---------------------------------------------------------------------------------

    private async void AddFolder_Click(object sender, RoutedEventArgs e)
    {
        if (_pickFolder is null || await _pickFolder() is not string folder)
        {
            return;
        }

        Root? existing = _roots.FirstOrDefault(root => string.Equals(root.Path, Normalise(folder), StringComparison.OrdinalIgnoreCase));

        if (existing is not null)
        {
            existing.Chip.IsChecked = true;
            return;
        }

        AddRoot(folder, on: true);
    }

    private void AddRoot(string folder, bool on)
    {
        string path = Normalise(folder);

        if (_roots.Any(root => string.Equals(root.Path, path, StringComparison.OrdinalIgnoreCase)))
        {
            return;
        }

        ToggleButton chip = new()
        {
            Content = path,
            IsChecked = false,
            CornerRadius = new CornerRadius(14),
            Padding = new Thickness(12, 4, 12, 4)
        };
        ToolTipService.SetToolTip(chip, $"Look for repositories under {path}");

        Root root = new(path, chip);
        _roots.Add(root);
        RootChips.Children.Add(chip);

        chip.Checked += (_, _) => RootToggled(root);
        chip.Unchecked += (_, _) => RootToggled(root);
        chip.IsChecked = on;
    }

    private void RootToggled(Root root)
    {
        if (root.Chip.IsChecked == true && !root.Scanned && !_pending.Contains(root))
        {
            _pending.Enqueue(root);
            _ = DrainAsync();
        }

        Refilter();
    }

    /// <summary>Scans the queued folders one at a time, so two walks never compete for the same disk.</summary>
    private async Task DrainAsync()
    {
        if (_scanning)
        {
            return;
        }

        _scanning = true;
        StopButton.Visibility = Visibility.Visible;
        ScanProgress.Visibility = Visibility.Visible;

        try
        {
            while (_pending.TryDequeue(out Root? root))
            {
                await ScanAsync(root);
            }
        }
        finally
        {
            _scanning = false;
            StopButton.Visibility = Visibility.Collapsed;
            ScanProgress.Visibility = Visibility.Collapsed;
            UpdateCount();
        }
    }

    private async Task ScanAsync(Root root)
    {
        _cancel = new CancellationTokenSource();
        CancellationToken token = _cancel.Token;
        int depth = (DepthBox.SelectedItem as ComboBoxItem)?.Tag is string tag && int.TryParse(tag, out int value)
            ? value
            : RepositoryScanner.DefaultMaxDepth;

        StatusText.Text = $"Looking in {root.Path}…";

        Progress<ScanProgress> progress = new(report =>
            StatusText.Text = $"Looking in {root.Path}… {report.FoldersVisited:N0} folders, {report.Found:N0} found");

        try
        {
            await RepositoryScanner.ScanAsync(root.Path, depth, progress, token, repository =>
                DispatcherQueue.TryEnqueue(() => AddFound(root, repository)));

            root.Scanned = true;
            StatusText.Text = root.Paths.Count switch
            {
                0 => $"No repositories under {root.Path}. Try looking deeper, or another folder.",
                1 => $"1 repository under {root.Path}.",
                _ => $"{root.Paths.Count} repositories under {root.Path}."
            };
        }
        catch (OperationCanceledException)
        {
            _pending.Clear();
            StatusText.Text = "Stopped. What was found so far is listed.";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            StatusText.Text = $"Could not finish {root.Path}: {ex.Message}";
        }
    }

    private void AddFound(Root root, DiscoveredRepository repository)
    {
        root.Paths.Add(repository.Path);

        if (_found.ContainsKey(repository.Path))
        {
            return;
        }

        repository.IsAlreadyAdded = _known.Contains(Normalise(repository.Path));

        if (repository.IsAlreadyAdded)
        {
            repository.IsSelected = false;
        }

        _found[repository.Path] = repository;

        if (root.Chip.IsChecked == true && Matches(repository))
        {
            _shown.Add(repository);
        }

        UpdateCount();
    }

    private void Stop_Click(object sender, RoutedEventArgs e) => CancelScans();

    // ---- Filter and selection --------------------------------------------------------------------

    private void FilterBox_TextChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args) => Refilter();

    private void Refilter()
    {
        HashSet<string> on = new(
            _roots.Where(root => root.Chip.IsChecked == true).SelectMany(root => root.Paths),
            StringComparer.OrdinalIgnoreCase);

        _shown.Clear();

        foreach (DiscoveredRepository repository in _found.Values
            .Where(repository => on.Contains(repository.Path) && Matches(repository))
            .OrderBy(repository => repository.IsAlreadyAdded)
            .ThenBy(repository => repository.Path, StringComparer.OrdinalIgnoreCase))
        {
            _shown.Add(repository);
        }

        UpdateCount();
    }

    private bool Matches(DiscoveredRepository repository)
    {
        string[] words = FilterBox.Text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return words.All(word => repository.Path.Contains(word, StringComparison.OrdinalIgnoreCase));
    }

    private void SelectShown_Click(object sender, RoutedEventArgs e)
    {
        foreach (DiscoveredRepository repository in _shown.Where(repository => repository.CanSelect))
        {
            repository.IsSelected = true;
        }

        UpdateCount();
    }

    private void SelectNone_Click(object sender, RoutedEventArgs e)
    {
        foreach (DiscoveredRepository repository in _found.Values)
        {
            repository.IsSelected = false;
        }

        UpdateCount();
    }

    private void Row_Toggled(object sender, RoutedEventArgs e) => UpdateCount();

    /// <summary>Checked, not already on Home, and in a folder that is on — whether or not the filter shows it.</summary>
    private List<DiscoveredRepository> Selected()
    {
        HashSet<string> on = new(
            _roots.Where(root => root.Chip.IsChecked == true).SelectMany(root => root.Paths),
            StringComparer.OrdinalIgnoreCase);

        return
        [
            .. _found.Values
                .Where(repository => repository.IsSelected && repository.CanSelect && on.Contains(repository.Path))
                .OrderBy(repository => repository.Path, StringComparer.OrdinalIgnoreCase)
        ];
    }

    private void UpdateCount()
    {
        int found = _found.Count;
        int already = _found.Values.Count(repository => repository.IsAlreadyAdded);
        int selected = Selected().Count;

        CountText.Text = found == 0
            ? ""
            : already > 0
                ? $"{selected} of {found} selected · {already} already on Home"
                : $"{selected} of {found} selected";

        UpdateProjectHint();
        SelectionChanged?.Invoke(this, EventArgs.Empty);
    }

    // ---- Projects --------------------------------------------------------------------------------

    private void ProjectBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        bool isNew = (ProjectBox.SelectedItem as ComboBoxItem)?.Tag as string == NewProject;
        NewProjectBox.Visibility = isNew ? Visibility.Visible : Visibility.Collapsed;

        if (isNew)
        {
            NewProjectBox.Focus(FocusState.Programmatic);
        }

        UpdateProjectHint();
    }

    private void UpdateProjectHint()
    {
        if (ProjectHint is null || (ProjectBox.SelectedItem as ComboBoxItem)?.Tag as string != FolderProjects)
        {
            if (ProjectHint is not null)
            {
                ProjectHint.Text = "";
            }

            return;
        }

        int folders = Selected().Select(repository => repository.SuggestedProject)
            .Where(name => name.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Count();

        ProjectHint.Text = folders switch
        {
            0 => "None of these sit in a subfolder, so no project is made.",
            1 => "1 project, named after its folder.",
            _ => $"{folders} projects, each named after its folder."
        };
    }

    private static string Normalise(string path) =>
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));

    /// <summary>A folder offered for scanning, its chip, and what its scan found.</summary>
    private sealed class Root(string path, ToggleButton chip)
    {
        public string Path { get; } = path;

        public ToggleButton Chip { get; } = chip;

        public bool Scanned { get; set; }

        public HashSet<string> Paths { get; } = new(StringComparer.OrdinalIgnoreCase);
    }
}
