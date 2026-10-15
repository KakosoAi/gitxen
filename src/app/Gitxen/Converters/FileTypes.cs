// SPDX-License-Identifier: GPL-3.0-or-later
using System.Globalization;
using Gitxen.Services;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace Gitxen.Converters;

/// <summary>
///  A file's type as a colour, for the dot beside it in a list. Function bindings for <c>x:Bind</c>,
///  e.g. <c>Fill="{x:Bind conv:FileTypes.DotFor(Name)}"</c>.
/// </summary>
/// <remarks>
///  The colours are the language colours the Overview already uses, so a C# file is the same green
///  in the changed-files list as in the language breakdown. Brushes are cached per colour.
/// </remarks>
public static class FileTypes
{
    private static readonly Dictionary<string, SolidColorBrush> Cache = new(StringComparer.OrdinalIgnoreCase);

    public static Brush DotFor(string path)
    {
        string hex = LanguageMap.Of(path ?? "").Color;

        if (!Cache.TryGetValue(hex, out SolidColorBrush? brush))
        {
            brush = new SolidColorBrush(Color.FromArgb(
                0xFF,
                byte.Parse(hex.AsSpan(1, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture),
                byte.Parse(hex.AsSpan(3, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture),
                byte.Parse(hex.AsSpan(5, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture)));
            Cache[hex] = brush;
        }

        return brush;
    }
}
