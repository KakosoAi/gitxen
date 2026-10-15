// SPDX-License-Identifier: GPL-3.0-or-later
using System.Globalization;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;

namespace Gitxen.ViewModels;

/// <summary>One file or folder in the Files section.</summary>
/// <remarks>
///  The row is shown at once from what the file system says; the shell's icon and type name arrive
///  a moment later and replace the glyph and the extension-based guess.
/// </remarks>
public sealed class FileEntryRow : ObservableObject
{
    private ImageSource? _icon;
    private string _typeName;

    public FileEntryRow(FileSystemInfo info, string relativePath)
    {
        Name = info.Name;
        FullPath = info.FullName;
        RelativePath = relativePath;
        IsFolder = info is DirectoryInfo;
        Size = info is FileInfo file ? file.Length : -1;
        Modified = info.LastWriteTime;
        _typeName = IsFolder ? "File folder" : GuessType(info.Name);
    }

    public string Name { get; }

    public string FullPath { get; }

    /// <summary>From the repository root, forward slashes: the form git speaks.</summary>
    public string RelativePath { get; }

    public bool IsFolder { get; }

    public long Size { get; }

    public DateTime Modified { get; }

    public string SizeText => IsFolder ? "" : FormatSize(Size);

    public string ModifiedText => Modified.ToString("g", CultureInfo.CurrentCulture);

    public string Glyph => IsFolder ? "" : "";

    public ImageSource? Icon
    {
        get => _icon;
        set
        {
            if (SetProperty(ref _icon, value))
            {
                OnPropertyChanged(nameof(IconVisibility));
                OnPropertyChanged(nameof(GlyphVisibility));
            }
        }
    }

    public Visibility IconVisibility => _icon is null ? Visibility.Collapsed : Visibility.Visible;

    public Visibility GlyphVisibility => _icon is null ? Visibility.Visible : Visibility.Collapsed;

    public string TypeName
    {
        get => _typeName;
        set => SetProperty(ref _typeName, value);
    }

    /// <summary>The change letter — M, A, D, R, or U for untracked — or a dot on a folder with changes inside.</summary>
    public string Status { get; set; } = "";

    public Brush? StatusBrush { get; set; }

    public string StatusTip { get; set; } = "";

    public bool IsIgnored { get; set; }

    public double RowOpacity => IsIgnored ? 0.5 : 1;

    public static string FormatSize(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} B",
        < 1024 * 1024 => $"{bytes / 1024.0:0.#} KB",
        < 1024L * 1024 * 1024 => $"{bytes / (1024.0 * 1024):0.#} MB",
        _ => $"{bytes / (1024.0 * 1024 * 1024):0.##} GB"
    };

    private static string GuessType(string name)
    {
        string extension = Path.GetExtension(name);
        return extension.Length > 1 ? $"{extension[1..].ToUpperInvariant()} file" : "File";
    }
}
