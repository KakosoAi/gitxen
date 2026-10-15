// SPDX-License-Identifier: GPL-3.0-or-later
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;

namespace Gitxen.Views;

/// <summary>One search hit in Settings: the card's title, its category's name and key, and the card.</summary>
public sealed record SettingsMatch(string Title, string Category, string CategoryKey, Border Card);

/// <summary>
///  Reads the settings cards straight out of the pages, so every setting is searchable by the words
///  written on it without a separate index to keep in step.
/// </summary>
/// <remarks>
///  Walks the logical content — panels, borders, content controls — rather than the visual tree, so a
///  category that has not been shown yet (and so has not been laid out) is searchable all the same.
/// </remarks>
internal static class SettingsSearch
{
    /// <summary>Every settings card under <paramref name="root"/>: the borders drawn with CardBorderStyle.</summary>
    public static IEnumerable<Border> Cards(DependencyObject root)
    {
        Style? cardStyle = Application.Current.Resources.TryGetValue("CardBorderStyle", out object? style) ? style as Style : null;

        return Descendants(root).OfType<Border>().Where(border => cardStyle is not null && ReferenceEquals(border.Style, cardStyle));
    }

    /// <summary>The card's heading: its first semibold text, else its first text.</summary>
    public static string Title(Border card)
    {
        List<TextBlock> texts = [.. Descendants(card).OfType<TextBlock>().Where(text => Text(text).Length > 0)];

        TextBlock? heading = texts.FirstOrDefault(text => text.FontWeight.Weight >= FontWeights.SemiBold.Weight) ?? texts.FirstOrDefault();
        return heading is null ? "Setting" : Text(heading);
    }

    /// <summary>Everything a person might search for on a card: its text, labels, placeholders and choices.</summary>
    public static IEnumerable<string> Words(Border card)
    {
        foreach (DependencyObject element in Descendants(card))
        {
            switch (element)
            {
                case TextBlock text:
                    yield return Text(text);
                    break;
                case ToggleSwitch toggle when toggle.Header is string header:
                    yield return header;
                    break;
                case TextBox box:
                    yield return box.Header as string ?? "";
                    yield return box.PlaceholderText;
                    break;
                case NumberBox number:
                    yield return number.Header as string ?? "";
                    break;
                case ComboBox combo:
                    yield return combo.Header as string ?? "";

                    foreach (object item in combo.Items)
                    {
                        yield return (item as ComboBoxItem)?.Content as string ?? item as string ?? "";
                    }

                    break;
                case ContentControl { Content: string content }:
                    yield return content;
                    break;
            }

            if (element is FrameworkElement framework && ToolTipService.GetToolTip(framework) is string tip)
            {
                yield return tip;
            }
        }
    }

    private static string Text(TextBlock block) =>
        block.Text.Length > 0 ? block.Text : string.Concat(block.Inlines.OfType<Run>().Select(run => run.Text));

    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        Stack<DependencyObject> pending = new();
        pending.Push(root);

        while (pending.Count > 0)
        {
            DependencyObject current = pending.Pop();
            yield return current;

            IEnumerable<object?> children = current switch
            {
                Panel panel => panel.Children,
                Border border => [border.Child],
                UserControl control => [control.Content],
                ContentControl control => [control.Content],
                ItemsControl items => items.Items,
                _ => []
            };

            foreach (object? child in children.Reverse())
            {
                if (child is DependencyObject next)
                {
                    pending.Push(next);
                }
            }
        }
    }
}
