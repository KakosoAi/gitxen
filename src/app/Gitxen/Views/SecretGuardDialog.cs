// SPDX-License-Identifier: GPL-3.0-or-later
using Gitxen.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Gitxen.Views;

/// <summary>What to do about what the guard found.</summary>
internal enum GuardChoice
{
    Cancel,
    Proceed,
    Unstage
}

/// <summary>
///  The warning before a commit or a push that carries a credential, a file that usually holds one, or
///  a file too big for the repository. The secret itself is never shown — only enough to recognise it.
/// </summary>
internal static class SecretGuardDialog
{
    public static async Task<GuardChoice> ShowAsync(XamlRoot root, IReadOnlyList<ScanFinding> findings, bool forPush)
    {
        StackPanel list = new() { Spacing = 10 };

        foreach (IGrouping<string, ScanFinding> file in findings.GroupBy(finding => finding.Path))
        {
            StackPanel group = new() { Spacing = 2 };
            group.Children.Add(new TextBlock { Text = file.Key, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis });

            foreach (ScanFinding finding in file)
            {
                string where = finding.Line > 0 ? $"line {finding.Line}: " : "";
                string excerpt = finding.Excerpt.Length > 0 ? $"  {finding.Excerpt}" : "";
                group.Children.Add(new TextBlock
                {
                    Text = $"{Icon(finding.Kind)}  {where}{finding.Rule}{excerpt}",
                    FontSize = 12,
                    Opacity = finding.Kind == FindingKind.PossibleSecret ? 0.75 : 0.95,
                    TextWrapping = TextWrapping.Wrap
                });
            }

            list.Children.Add(group);
        }

        bool secrets = findings.Any(finding => finding.Kind is FindingKind.Secret or FindingKind.SensitiveFile);
        bool large = findings.Any(finding => finding.Kind == FindingKind.LargeFile);

        string advice = forPush
            ? "These are in commits that have not been pushed yet. Once pushed, a secret is public to everyone who can read the repository and has to be revoked, not just deleted. To take it out, undo the commit or amend it, then push."
            : "A committed secret stays in history even after it is deleted, and has to be revoked. Unstaging keeps your files as they are, just out of this commit; files such as .env are also added to .gitignore.";

        if (large)
        {
            advice += " Large files make every clone slower; GitHub refuses files over 100 MB. Git LFS is meant for them.";
        }

        StackPanel content = new() { Spacing = 12, Width = 520 };
        content.Children.Add(new TextBlock { Text = advice, TextWrapping = TextWrapping.Wrap, Opacity = 0.8 });
        content.Children.Add(new ScrollViewer { Content = list, MaxHeight = 320 });

        ContentDialog dialog = new()
        {
            Title = secrets ? (forPush ? "This push contains secrets" : "This commit contains secrets") : (large ? "Large files" : "Possible secrets"),
            Content = content,
            PrimaryButtonText = forPush ? "Push anyway" : "Commit anyway",
            SecondaryButtonText = forPush ? "" : "Unstage flagged files",
            CloseButtonText = "Cancel",
            DefaultButton = forPush ? ContentDialogButton.Close : ContentDialogButton.Secondary,
            XamlRoot = root
        };

        return await dialog.ShowAsync() switch
        {
            ContentDialogResult.Primary => GuardChoice.Proceed,
            ContentDialogResult.Secondary => GuardChoice.Unstage,
            _ => GuardChoice.Cancel
        };
    }

    private static string Icon(FindingKind kind) => kind switch
    {
        FindingKind.Secret => "⛔",
        FindingKind.SensitiveFile => "\U0001F511",
        FindingKind.LargeFile => "\U0001F4E6",
        _ => "⚠"
    };
}
