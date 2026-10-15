// SPDX-License-Identifier: GPL-3.0-or-later
using System.Globalization;
using System.Text.Json;

namespace Gitxen.Terminal;

/// <summary>
///  The font, size, cursor and colours the user's Windows Terminal uses, so the pane looks the same —
///  a Nerd Font in particular, which is what an Oh My Posh prompt needs to draw its icons.
/// </summary>
/// <remarks>
///  Read from Windows Terminal's <c>settings.json</c>: the profile defaults, then the default profile
///  on top. A built-in colour scheme is not written into that file, so the common ones are listed
///  here; anything unknown falls back to Campbell, Windows Terminal's own default. Nothing is
///  required: without Windows Terminal the pane uses Cascadia Mono and Campbell.
/// </remarks>
public sealed record WindowsTerminalLook(string FontFamily, double FontSize, string Cursor, IReadOnlyDictionary<string, string> Theme)
{
    private const string FallbackFonts = "'Cascadia Mono', Consolas, monospace";

    private static readonly Dictionary<string, string[]> BuiltInSchemes = new(StringComparer.OrdinalIgnoreCase)
    {
        // background, foreground, then black red green yellow blue purple cyan white, then the bright eight.
        ["Campbell"] =
        [
            "#0C0C0C", "#CCCCCC",
            "#0C0C0C", "#C50F1F", "#13A10E", "#C19C00", "#0037DA", "#881798", "#3A96DD", "#CCCCCC",
            "#767676", "#E74856", "#16C60C", "#F9F1A5", "#3B78FF", "#B4009E", "#61D6D6", "#F2F2F2"
        ],
        ["Campbell Powershell"] =
        [
            "#012456", "#CCCCCC",
            "#0C0C0C", "#C50F1F", "#13A10E", "#C19C00", "#0037DA", "#881798", "#3A96DD", "#CCCCCC",
            "#767676", "#E74856", "#16C60C", "#F9F1A5", "#3B78FF", "#B4009E", "#61D6D6", "#F2F2F2"
        ],
        ["One Half Dark"] =
        [
            "#282C34", "#DCDFE4",
            "#282C34", "#E06C75", "#98C379", "#E5C07B", "#61AFEF", "#C678DD", "#56B6C2", "#DCDFE4",
            "#5A6374", "#E06C75", "#98C379", "#E5C07B", "#61AFEF", "#C678DD", "#56B6C2", "#DCDFE4"
        ],
        ["One Half Light"] =
        [
            "#FAFAFA", "#383A42",
            "#383A42", "#E45649", "#50A14F", "#C18301", "#0184BC", "#A626A4", "#0997B3", "#FAFAFA",
            "#4F525D", "#DF6C75", "#98C379", "#E4C07A", "#61AFEF", "#C577DD", "#56B5C1", "#FFFFFF"
        ],
    };

    private static readonly string[] PaletteKeys =
    [
        "black", "red", "green", "yellow", "blue", "magenta", "cyan", "white",
        "brightBlack", "brightRed", "brightGreen", "brightYellow", "brightBlue", "brightMagenta", "brightCyan", "brightWhite"
    ];

    /// <summary>The background, for the host behind the page so a resize never flashes white.</summary>
    public string Background => Theme.TryGetValue("background", out string? background) ? background : "#0C0C0C";

    public static WindowsTerminalLook Load()
    {
        string font = "";
        double size = 10;
        string cursor = "";
        string scheme = "Campbell";
        JsonElement? customSchemes = null;

        try
        {
            if (SettingsPath() is string path)
            {
                using JsonDocument document = JsonDocument.Parse(
                    File.ReadAllText(path),
                    new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });

                JsonElement root = document.RootElement;
                customSchemes = root.TryGetProperty("schemes", out JsonElement schemes) ? schemes.Clone() : null;

                if (root.TryGetProperty("profiles", out JsonElement profiles) && profiles.ValueKind == JsonValueKind.Object)
                {
                    if (profiles.TryGetProperty("defaults", out JsonElement defaults))
                    {
                        Apply(defaults, ref font, ref size, ref cursor, ref scheme);
                    }

                    string defaultProfile = root.TryGetProperty("defaultProfile", out JsonElement id) ? id.GetString() ?? "" : "";

                    if (profiles.TryGetProperty("list", out JsonElement list) && list.ValueKind == JsonValueKind.Array)
                    {
                        foreach (JsonElement profile in list.EnumerateArray())
                        {
                            if (profile.TryGetProperty("guid", out JsonElement guid)
                                && string.Equals(guid.GetString(), defaultProfile, StringComparison.OrdinalIgnoreCase))
                            {
                                Apply(profile, ref font, ref size, ref cursor, ref scheme);
                            }
                        }
                    }
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            // A settings file that cannot be read leaves the defaults; the terminal still works.
        }

        return new WindowsTerminalLook(
            font.Length > 0 ? $"'{font.Replace("'", "", StringComparison.Ordinal)}', {FallbackFonts}" : FallbackFonts,
            Math.Round(Math.Clamp(size, 6, 36) * 96 / 72, 1),
            cursor switch
            {
                "bar" => "bar",
                "underscore" or "doubleUnderscore" or "vintage" => "underline",
                _ => "block"
            },
            ThemeFor(scheme, customSchemes));
    }

    private static void Apply(JsonElement profile, ref string font, ref double size, ref string cursor, ref string scheme)
    {
        if (profile.TryGetProperty("font", out JsonElement fontObject) && fontObject.ValueKind == JsonValueKind.Object)
        {
            if (fontObject.TryGetProperty("face", out JsonElement face) && face.GetString() is { Length: > 0 } faceName)
            {
                font = faceName;
            }

            if (fontObject.TryGetProperty("size", out JsonElement fontSize) && fontSize.TryGetDouble(out double points))
            {
                size = points;
            }
        }

        // The settings format before Windows Terminal 1.10.
        if (profile.TryGetProperty("fontFace", out JsonElement legacyFace) && legacyFace.GetString() is { Length: > 0 } legacy)
        {
            font = legacy;
        }

        if (profile.TryGetProperty("fontSize", out JsonElement legacySize) && legacySize.TryGetDouble(out double legacyPoints))
        {
            size = legacyPoints;
        }

        if (profile.TryGetProperty("cursorShape", out JsonElement shape) && shape.GetString() is { Length: > 0 } shapeName)
        {
            cursor = shapeName;
        }

        if (profile.TryGetProperty("colorScheme", out JsonElement colorScheme) && colorScheme.ValueKind == JsonValueKind.String
            && colorScheme.GetString() is { Length: > 0 } name)
        {
            scheme = name;
        }
    }

    private static Dictionary<string, string> ThemeFor(string scheme, JsonElement? customSchemes)
    {
        if (customSchemes is { ValueKind: JsonValueKind.Array } list)
        {
            foreach (JsonElement custom in list.EnumerateArray())
            {
                if (custom.TryGetProperty("name", out JsonElement name)
                    && string.Equals(name.GetString(), scheme, StringComparison.OrdinalIgnoreCase))
                {
                    return FromCustom(custom);
                }
            }
        }

        string[] colours = BuiltInSchemes.TryGetValue(scheme, out string[]? known) ? known : BuiltInSchemes["Campbell"];

        Dictionary<string, string> theme = new()
        {
            ["background"] = colours[0],
            ["foreground"] = colours[1],
            ["cursor"] = colours[1],
            ["selectionBackground"] = "#FFFFFF40"
        };

        for (int i = 0; i < PaletteKeys.Length; i++)
        {
            theme[PaletteKeys[i]] = colours[i + 2];
        }

        return theme;
    }

    /// <summary>Windows Terminal's scheme keys, renamed where xterm.js calls them something else.</summary>
    private static Dictionary<string, string> FromCustom(JsonElement custom)
    {
        Dictionary<string, string> theme = [];

        foreach (JsonProperty property in custom.EnumerateObject())
        {
            if (property.Value.ValueKind != JsonValueKind.String || property.Name == "name")
            {
                continue;
            }

            string key = property.Name switch
            {
                "purple" => "magenta",
                "brightPurple" => "brightMagenta",
                "cursorColor" => "cursor",
                _ => property.Name
            };

            theme[key] = property.Value.GetString() ?? "";
        }

        theme.TryAdd("selectionBackground", "#FFFFFF40");
        return theme;
    }

    /// <summary>The settings of the Store, Preview or unpackaged Windows Terminal, whichever exists first.</summary>
    private static string? SettingsPath()
    {
        string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

        string[] candidates =
        [
            Path.Combine(local, "Packages", "Microsoft.WindowsTerminal_8wekyb3d8bbwe", "LocalState", "settings.json"),
            Path.Combine(local, "Packages", "Microsoft.WindowsTerminalPreview_8wekyb3d8bbwe", "LocalState", "settings.json"),
            Path.Combine(local, "Microsoft", "Windows Terminal", "settings.json"),
        ];

        return candidates.FirstOrDefault(File.Exists);
    }

    /// <summary>For the init message: CSS pixels, invariant, so a comma-decimal culture cannot break it.</summary>
    public string FontSizeText => FontSize.ToString(CultureInfo.InvariantCulture);
}
