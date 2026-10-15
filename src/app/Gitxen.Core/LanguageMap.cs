// SPDX-License-Identifier: GPL-3.0-or-later
namespace Gitxen.Services;

/// <summary>What sort of file something is, for the Overview's breakdown.</summary>
internal enum FileKind
{
    Code,
    Markup,
    Data,
    Docs,
    Media,
    Other
}

/// <summary>A language as the Overview shows it: its name, the colour it is usually drawn in, and its kind.</summary>
internal sealed record LanguageInfo(string Name, string Color, FileKind Kind);

/// <summary>
///  Recognises a file's language from its name. Deliberately a table, not a detector: an extension
///  says enough for a share-of-the-repository chart, and reading content to tell C from C++ headers
///  would cost more than the answer is worth.
/// </summary>
/// <remarks>
///  The colours are the ones GitHub draws each language in, so a repository's chart looks the way
///  its owner already knows it; a language's colour follows the language, never its rank.
/// </remarks>
internal static class LanguageMap
{
    private static readonly LanguageInfo Other = new("Other", "#8b8b8b", FileKind.Other);

    private static readonly Dictionary<string, LanguageInfo> ByExtension = Build(
        ("C#", "#178600", FileKind.Code, [".cs", ".csx"]),
        ("F#", "#b845fc", FileKind.Code, [".fs", ".fsi", ".fsx"]),
        ("Visual Basic", "#945db7", FileKind.Code, [".vb"]),
        ("TypeScript", "#3178c6", FileKind.Code, [".ts", ".tsx", ".mts", ".cts"]),
        ("JavaScript", "#f1e05a", FileKind.Code, [".js", ".jsx", ".mjs", ".cjs"]),
        ("Python", "#3572a5", FileKind.Code, [".py", ".pyi", ".pyw"]),
        ("Java", "#b07219", FileKind.Code, [".java"]),
        ("Kotlin", "#a97bff", FileKind.Code, [".kt", ".kts"]),
        ("Go", "#00add8", FileKind.Code, [".go"]),
        ("Rust", "#dea584", FileKind.Code, [".rs"]),
        ("C", "#555555", FileKind.Code, [".c", ".h"]),
        ("C++", "#f34b7d", FileKind.Code, [".cpp", ".cc", ".cxx", ".hpp", ".hh", ".hxx", ".inl"]),
        ("Objective-C", "#438eff", FileKind.Code, [".m", ".mm"]),
        ("Swift", "#f05138", FileKind.Code, [".swift"]),
        ("Dart", "#00b4ab", FileKind.Code, [".dart"]),
        ("Ruby", "#701516", FileKind.Code, [".rb", ".rake", ".gemspec"]),
        ("PHP", "#4f5d95", FileKind.Code, [".php"]),
        ("Scala", "#c22d40", FileKind.Code, [".scala", ".sc"]),
        ("Lua", "#000080", FileKind.Code, [".lua"]),
        ("R", "#198ce7", FileKind.Code, [".r"]),
        ("Elixir", "#6e4a7e", FileKind.Code, [".ex", ".exs"]),
        ("Haskell", "#5e5086", FileKind.Code, [".hs"]),
        ("Zig", "#ec915c", FileKind.Code, [".zig"]),
        ("PowerShell", "#012456", FileKind.Code, [".ps1", ".psm1", ".psd1"]),
        ("Shell", "#89e051", FileKind.Code, [".sh", ".bash", ".zsh", ".fish"]),
        ("Batchfile", "#c1f12e", FileKind.Code, [".bat", ".cmd"]),
        ("SQL", "#e38c00", FileKind.Code, [".sql"]),
        ("Vue", "#41b883", FileKind.Code, [".vue"]),
        ("Svelte", "#ff3e00", FileKind.Code, [".svelte"]),
        ("HTML", "#e34c26", FileKind.Markup, [".html", ".htm", ".cshtml", ".razor"]),
        ("CSS", "#663399", FileKind.Markup, [".css"]),
        ("SCSS", "#c6538c", FileKind.Markup, [".scss", ".sass", ".less"]),
        ("XAML", "#0c54c2", FileKind.Markup, [".xaml", ".axaml"]),
        ("XML", "#0060ac", FileKind.Data, [".xml", ".xsd", ".xsl", ".resx", ".wxs", ".wxi", ".plist"]),
        ("MSBuild", "#512bd4", FileKind.Data, [".csproj", ".fsproj", ".vbproj", ".props", ".targets", ".slnx", ".sln", ".proj"]),
        ("JSON", "#292929", FileKind.Data, [".json", ".jsonc", ".json5"]),
        ("YAML", "#cb171e", FileKind.Data, [".yml", ".yaml"]),
        ("TOML", "#9c4221", FileKind.Data, [".toml"]),
        ("INI", "#d1dbe0", FileKind.Data, [".ini", ".cfg", ".conf", ".editorconfig", ".gitattributes", ".gitignore", ".env"]),
        ("CSV", "#237346", FileKind.Data, [".csv", ".tsv"]),
        ("Markdown", "#083fa1", FileKind.Docs, [".md", ".markdown", ".mdx"]),
        ("Text", "#8b8b8b", FileKind.Docs, [".txt", ".rst", ".adoc", ".rtf"]),
        ("Images", "#a0a0a0", FileKind.Media, [".png", ".jpg", ".jpeg", ".gif", ".bmp", ".ico", ".svg", ".webp", ".tif", ".tiff", ".psd"]),
        ("Media", "#a0a0a0", FileKind.Media, [".mp3", ".wav", ".ogg", ".mp4", ".mov", ".webm", ".ttf", ".otf", ".woff", ".woff2"]));

    private static readonly Dictionary<string, LanguageInfo> ByName = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Dockerfile"] = new("Dockerfile", "#384d54", FileKind.Code),
        ["Makefile"] = new("Makefile", "#427819", FileKind.Code),
        ["CMakeLists.txt"] = new("CMake", "#da3434", FileKind.Code),
        ["LICENSE"] = new("Text", "#8b8b8b", FileKind.Docs),
    };

    /// <summary>Dependency folders, build output and lock files: in the repository, but nobody wrote them.</summary>
    private static readonly string[] GeneratedMarkers =
    [
        "node_modules/", "vendor/", "dist/", "bower_components/", "third_party/", "third-party/", "wwwroot/lib/"
    ];

    private static readonly HashSet<string> LockFiles = new(StringComparer.OrdinalIgnoreCase)
    {
        "package-lock.json", "yarn.lock", "pnpm-lock.yaml", "Cargo.lock", "poetry.lock", "Gemfile.lock",
        "composer.lock", "go.sum", "packages.lock.json", "Podfile.lock", "pubspec.lock"
    };

    public static LanguageInfo Of(string path)
    {
        string name = path[(path.LastIndexOf('/') + 1)..];

        if (ByName.TryGetValue(name, out LanguageInfo? named))
        {
            return named;
        }

        string extension = Path.GetExtension(name);

        // ".gitignore" and friends are all extension.
        if (extension.Length == 0 && name.StartsWith('.'))
        {
            extension = name;
        }

        return ByExtension.TryGetValue(extension, out LanguageInfo? language) ? language : Other;
    }

    /// <summary>Whether a file was generated or vendored, and so should not count towards the languages.</summary>
    public static bool IsGenerated(string path)
    {
        string name = path[(path.LastIndexOf('/') + 1)..];

        return LockFiles.Contains(name)
            || name.EndsWith(".min.js", StringComparison.OrdinalIgnoreCase)
            || name.EndsWith(".min.css", StringComparison.OrdinalIgnoreCase)
            || name.EndsWith(".designer.cs", StringComparison.OrdinalIgnoreCase)
            || name.EndsWith(".g.cs", StringComparison.OrdinalIgnoreCase)
            || GeneratedMarkers.Any(marker => path.StartsWith(marker, StringComparison.OrdinalIgnoreCase) || path.Contains("/" + marker, StringComparison.OrdinalIgnoreCase));
    }

    private static Dictionary<string, LanguageInfo> Build(params (string Name, string Color, FileKind Kind, string[] Extensions)[] entries)
    {
        Dictionary<string, LanguageInfo> map = new(StringComparer.OrdinalIgnoreCase);

        foreach ((string name, string color, FileKind kind, string[] extensions) in entries)
        {
            LanguageInfo info = new(name, color, kind);

            foreach (string extension in extensions)
            {
                map[extension] = info;
            }
        }

        return map;
    }
}
