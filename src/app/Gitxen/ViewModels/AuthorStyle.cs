// SPDX-License-Identifier: GPL-3.0-or-later
using Microsoft.UI;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace Gitxen.ViewModels;

/// <summary>
///  A colour for each author, the same one wherever they appear: the history rows, the author chips
///  above them and the details card.
/// </summary>
/// <remarks>
///  Derived from the name, so nothing has to be stored and the same person is the same colour in every
///  repository and every session. Mid-tone fills that carry white initials in both light and dark.
/// </remarks>
public static class AuthorStyle
{
    private static readonly SolidColorBrush[] Palette =
    [
        Brush(0xE5, 0x48, 0x4D),
        Brush(0xF7, 0x68, 0x08),
        Brush(0xC9, 0x85, 0x00),
        Brush(0x30, 0xA4, 0x6C),
        Brush(0x12, 0xA5, 0x94),
        Brush(0x00, 0x91, 0xFF),
        Brush(0x6E, 0x56, 0xCF),
        Brush(0xD6, 0x40, 0x9F),
    ];

    private static readonly SolidColorBrush White = new(Colors.White);

    /// <summary>The author's fill.</summary>
    public static Brush FillFor(string author) => Palette[IndexFor(author)];

    /// <summary>What initials are drawn in on <see cref="FillFor"/>.</summary>
    public static Brush InitialsBrush => White;

    /// <summary>Stable across runs: <see cref="string.GetHashCode()"/> is not, so the characters are summed instead.</summary>
    private static int IndexFor(string author)
    {
        int hash = 17;

        foreach (char character in author.Trim().ToLowerInvariant())
        {
            hash = unchecked((hash * 31) + character);
        }

        return (int)((uint)hash % (uint)Palette.Length);
    }

    private static SolidColorBrush Brush(byte red, byte green, byte blue) => new(Color.FromArgb(0xFF, red, green, blue));
}
