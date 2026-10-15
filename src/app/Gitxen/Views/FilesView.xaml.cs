// SPDX-License-Identifier: GPL-3.0-or-later
using System.Diagnostics;
using Gitxen.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;
using Windows.Storage.FileProperties;

namespace Gitxen.Views;

/// <summary>
///  The repository's files as a folder view: the shell's icons and type names, each entry's git
///  status, what git ignores (dimmed, or hidden), and a preview of the selected file.
/// </summary>
/// <remarks>
///  <para>
///   One folder at a time, read when it is shown. A tree of the whole working copy would mean walking
///   <c>node_modules</c> and build output to draw it; a folder costs one directory listing and one
///   <c>check-ignore</c>.
///  </para>
///  <para>
///   Status is the Changes section's listing, re-read on arriving here the way that section does, so
///   the two pages always agree.
///  </para>
/// </remarks>
public sealed partial class FilesView : RepositoryPage
{
    /// <summary>Beyond this, a folder is listed but only the first entries get shell icons.</summary>
    private const int IconLimit = 400;

    /// <summary>Text previews stop here; a larger file is opened, not read into a TextBlock.</summary>
    private const long PreviewLimit = 512 * 1024;

    private static readonly HashSet<string> ImageExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".png", ".jpg", ".jpeg", ".gif", ".bmp", ".ico", ".webp", ".tif", ".tiff"
    };

    private readonly List<FileEntryRow> _entries = [];
    private string _folder = "";
    private string _root = "";
    private int _generation;

    public FilesView()
    {
        InitializeComponent();
    }

    public override async Task ActivateAsync()
    {
        if (Tab is RepositoryTabViewModel tab)
        {
            await tab.Changes.RefreshAsync();
        }

        await LoadAsync();
    }

    protected override void OnTabChanged()
    {
        _root = Tab?.WorkingDir ?? "";
        _folder = "";
        ClosePreview();

        // Restored straight into this section, the page is activated before its tab arrives.
        if (Visibility == Visibility.Visible && Tab is not null)
        {
            _ = ActivateAsync();
        }
    }

    private static string Combine(string folder, string name) => folder.Length == 0 ? name : $"{folder}/{name}";

    private async Task LoadAsync()
    {
        if (Tab is not RepositoryTabViewModel tab || _root.Length == 0)
        {
            return;
        }

        int generation = ++_generation;
        string folder = _folder;
        string directory = Path.Combine(_root, folder.Replace('/', Path.DirectorySeparatorChar));

        if (!Directory.Exists(directory))
        {
            _folder = "";
            folder = "";
            directory = _root;
        }

        List<FileEntryRow> rows = await Task.Run(() => ReadFolder(directory, folder));

        // Inside an ignored folder everything is ignored; asking git about each entry is pointless.
        bool folderIgnored = folder.Length > 0 && (await tab.GetIgnoredAsync([folder])).Count > 0;
        HashSet<string> ignored = folderIgnored
            ? [.. rows.Select(row => row.RelativePath)]
            : await tab.GetIgnoredAsync([.. rows.Select(row => row.RelativePath)]);

        if (generation != _generation)
        {
            return;
        }

        MarkStatus(tab, rows, ignored);

        _entries.Clear();
        _entries.AddRange(rows);
        ShowEntries();
        ShowBreadcrumb();

        int ignoredCount = rows.Count(row => row.IsIgnored);
        Summary.Text = $"{rows.Count(row => row.IsFolder)} folders, {rows.Count(row => !row.IsFolder)} files" +
            (ignoredCount > 0 ? $" · {ignoredCount} ignored" : "");

        await LoadShellDetailsAsync(directory, rows, generation);
    }

    private static List<FileEntryRow> ReadFolder(string directory, string folder)
    {
        List<FileEntryRow> rows = [];

        try
        {
            DirectoryInfo info = new(directory);

            foreach (FileSystemInfo entry in info.EnumerateFileSystemInfos())
            {
                // The repository's own database is not one of its files.
                if (folder.Length == 0 && entry.Name.Equals(".git", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                rows.Add(new FileEntryRow(entry, Combine(folder, entry.Name)));
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A folder that cannot be read is shown empty rather than failing the page.
        }

        return [.. rows.OrderByDescending(row => row.IsFolder).ThenBy(row => row.Name, StringComparer.OrdinalIgnoreCase)];
    }

    private static void MarkStatus(RepositoryTabViewModel tab, List<FileEntryRow> rows, HashSet<string> ignored)
    {
        List<ChangedFileViewModel> changes = [.. tab.Changes.Staged, .. tab.Changes.Unstaged];

        foreach (FileEntryRow row in rows)
        {
            row.IsIgnored = ignored.Contains(row.RelativePath);

            if (row.IsFolder)
            {
                string prefix = row.RelativePath + "/";
                int inside = changes.Count(change => change.Name.StartsWith(prefix, StringComparison.Ordinal));

                if (inside > 0)
                {
                    row.Status = "•";
                    row.StatusBrush = changes.First(change => change.Name.StartsWith(prefix, StringComparison.Ordinal)).ChangeBrush;
                    row.StatusTip = inside == 1 ? "1 change inside" : $"{inside} changes inside";
                }

                continue;
            }

            if (changes.FirstOrDefault(change => change.Name == row.RelativePath) is ChangedFileViewModel changed)
            {
                bool untracked = changed.IsNew && !changed.IsStaged;
                row.Status = untracked ? "U" : changed.Change;
                row.StatusBrush = changed.ChangeBrush;
                row.StatusTip = untracked ? "Untracked" : $"{changed.Kind}{(changed.IsStaged ? ", staged" : "")}";
            }
            else if (row.IsIgnored)
            {
                row.StatusTip = "Ignored";
            }
        }
    }

    private void ShowEntries()
    {
        string filter = FilterBox.Text.Trim();
        bool showIgnored = IgnoredToggle.IsChecked == true;

        List<FileEntryRow> visible = [.. _entries.Where(row =>
            (showIgnored || !row.IsIgnored) &&
            (filter.Length == 0 || row.Name.Contains(filter, StringComparison.OrdinalIgnoreCase)))];

        EntryList.ItemsSource = visible;
        EmptyText.Text = _entries.Count == 0 ? "This folder is empty." : "Nothing here matches.";
        EmptyText.Visibility = visible.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        UpButton.IsEnabled = _folder.Length > 0;
    }

    private void ShowBreadcrumb()
    {
        List<string> crumbs = [Tab?.Title ?? Path.GetFileName(_root)];
        crumbs.AddRange(_folder.Split('/', StringSplitOptions.RemoveEmptyEntries));
        Breadcrumb.ItemsSource = crumbs;
    }

    /// <summary>The shell's icon and type name for each entry — what File Explorer would show.</summary>
    private async Task LoadShellDetailsAsync(string directory, List<FileEntryRow> rows, int generation)
    {
        try
        {
            StorageFolder folder = await StorageFolder.GetFolderFromPathAsync(directory);
            IReadOnlyList<IStorageItem> items = await folder.GetItemsAsync(0, IconLimit);
            Dictionary<string, FileEntryRow> byName = rows.ToDictionary(row => row.Name, StringComparer.OrdinalIgnoreCase);

            foreach (IStorageItem item in items)
            {
                if (generation != _generation)
                {
                    return;
                }

                if (!byName.TryGetValue(item.Name, out FileEntryRow? row))
                {
                    continue;
                }

                // A type with no friendly name comes back as its bare ProgID ("txtfile"): keep the guess.
                if (item is StorageFile file && file.DisplayType.Contains(' ', StringComparison.Ordinal))
                {
                    row.TypeName = file.DisplayType;
                }

                using StorageItemThumbnail? thumbnail = item is IStorageItemProperties properties
                    ? await properties.GetThumbnailAsync(ThumbnailMode.ListView, 20, ThumbnailOptions.UseCurrentScale)
                    : null;

                if (thumbnail is not null && thumbnail.Size > 0)
                {
                    BitmapImage image = new();
                    await image.SetSourceAsync(thumbnail);
                    row.Icon = image;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or System.Runtime.InteropServices.COMException)
        {
            // No shell details: the glyphs and the extension-based type stay.
        }
    }

    private void Navigate(string folder)
    {
        _folder = folder;
        FilterBox.Text = "";
        ClosePreview();
        _ = LoadAsync();
    }

    private void OpenEntry(FileEntryRow row)
    {
        if (row.IsFolder)
        {
            Navigate(row.RelativePath);
        }
        else
        {
            Launch(row.FullPath);
        }
    }

    private static void Launch(string path)
    {
        try
        {
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true })?.Dispose();
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            // No app is registered for the type; Windows has already said so.
        }
    }

    private async Task PreviewAsync(FileEntryRow row)
    {
        PreviewPane.Visibility = Visibility.Visible;
        PreviewName.Text = row.Name;
        PreviewDetail.Text = $"{row.TypeName} · {row.SizeText} · {row.ModifiedText}";
        TextPreview.Visibility = Visibility.Collapsed;
        ImagePreview.Visibility = Visibility.Collapsed;
        NoPreview.Visibility = Visibility.Collapsed;

        if (ImageExtensions.Contains(Path.GetExtension(row.Name)))
        {
            ImagePreview.Source = new BitmapImage(new Uri(row.FullPath));
            ImagePreview.Visibility = Visibility.Visible;
            return;
        }

        if (row.Size > PreviewLimit)
        {
            ShowNoPreview($"Too large to preview ({row.SizeText}). Open it instead.");
            return;
        }

        string? text = await Task.Run(() => ReadText(row.FullPath));

        if (PreviewName.Text != row.Name)
        {
            return;
        }

        if (text is null)
        {
            ShowNoPreview("A binary file — there is no text to show. Open it instead.");
            return;
        }

        PreviewText.Text = text;
        TextPreview.Visibility = Visibility.Visible;
        TextPreview.ChangeView(0, 0, null, true);
    }

    /// <summary>The file as text, or null when it looks binary: a NUL in the first 8 KB, as git decides.</summary>
    private static string? ReadText(string path)
    {
        try
        {
            byte[] bytes = File.ReadAllBytes(path);

            if (bytes.AsSpan(0, Math.Min(bytes.Length, 8000)).Contains((byte)0))
            {
                return null;
            }

            return System.Text.Encoding.UTF8.GetString(bytes).TrimStart('﻿');
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private void ShowNoPreview(string message)
    {
        NoPreview.Text = message;
        NoPreview.Visibility = Visibility.Visible;
    }

    private void ClosePreview()
    {
        PreviewPane.Visibility = Visibility.Collapsed;
        PreviewText.Text = "";
        ImagePreview.Source = null;
    }

    private static FileEntryRow? RowOf(object sender) => (sender as FrameworkElement)?.DataContext as FileEntryRow;

    private void EntryList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (EntryList.SelectedItem is FileEntryRow { IsFolder: false } row)
        {
            _ = PreviewAsync(row);
        }
    }

    private void Entry_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        if (RowOf(sender) is FileEntryRow row)
        {
            OpenEntry(row);
        }
    }

    private void EntryList_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == Windows.System.VirtualKey.Enter && EntryList.SelectedItem is FileEntryRow row)
        {
            e.Handled = true;
            OpenEntry(row);
        }
        else if (e.Key == Windows.System.VirtualKey.Back && _folder.Length > 0)
        {
            e.Handled = true;
            Up_Click(sender, e);
        }
    }

    private void Up_Click(object sender, RoutedEventArgs e)
    {
        int slash = _folder.LastIndexOf('/');
        Navigate(slash >= 0 ? _folder[..slash] : "");
    }

    private void Breadcrumb_ItemClicked(BreadcrumbBar sender, BreadcrumbBarItemClickedEventArgs args) =>
        Navigate(string.Join('/', _folder.Split('/', StringSplitOptions.RemoveEmptyEntries).Take(args.Index)));

    private void FilterBox_TextChanged(object sender, TextChangedEventArgs e) => ShowEntries();

    private void IgnoredToggle_Click(object sender, RoutedEventArgs e) => ShowEntries();

    private void Refresh_Click(object sender, RoutedEventArgs e) => _ = ActivateAsync();

    private void Explorer_Click(object sender, RoutedEventArgs e) =>
        Launch(Path.Combine(_root, _folder.Replace('/', Path.DirectorySeparatorChar)));

    private void OpenEntry_Click(object sender, RoutedEventArgs e)
    {
        if (RowOf(sender) is FileEntryRow row)
        {
            OpenEntry(row);
        }
    }

    private void RevealEntry_Click(object sender, RoutedEventArgs e)
    {
        if (RowOf(sender) is FileEntryRow row)
        {
            // explorer parses its own command line: the path is quoted inside the one argument.
            Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{row.FullPath}\""))?.Dispose();
        }
    }

    private void CopyPath_Click(object sender, RoutedEventArgs e) => Copy(RowOf(sender)?.FullPath);

    private void CopyRelativePath_Click(object sender, RoutedEventArgs e) => Copy(RowOf(sender)?.RelativePath);

    private static void Copy(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return;
        }

        DataPackage package = new();
        package.SetText(text);
        Clipboard.SetContent(package);
    }

    private void OpenSelected_Click(object sender, RoutedEventArgs e)
    {
        if (EntryList.SelectedItem is FileEntryRow row)
        {
            Launch(row.FullPath);
        }
    }

    private void ClosePreview_Click(object sender, RoutedEventArgs e) => ClosePreview();
}
