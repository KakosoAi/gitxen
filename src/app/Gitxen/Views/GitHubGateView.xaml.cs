// SPDX-License-Identifier: GPL-3.0-or-later
using Gitxen.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Gitxen.Views;

/// <summary>
///  The explanation a GitHub section shows when gh cannot be used, with the way out of it.
/// </summary>
public sealed partial class GitHubGateView : UserControl
{
    public static readonly DependencyProperty GitHubProperty = DependencyProperty.Register(
        nameof(GitHub),
        typeof(GitHubRepositoryViewModel),
        typeof(GitHubGateView),
        new PropertyMetadata(null));

    public GitHubGateView()
    {
        InitializeComponent();
    }

    /// <summary>The repository's GitHub state, set by the page that hosts this.</summary>
    public GitHubRepositoryViewModel? GitHub
    {
        get => (GitHubRepositoryViewModel?)GetValue(GitHubProperty);
        set => SetValue(GitHubProperty, value);
    }

    /// <summary>Raised after a re-check, so the page can load what it was waiting to show.</summary>
    public event EventHandler? Rechecked;

    private async void Recheck_Click(object sender, RoutedEventArgs e)
    {
        if (GitHub is null)
        {
            return;
        }

        await GitHub.RecheckAsync();
        Rechecked?.Invoke(this, EventArgs.Empty);
    }

    private void Settings_Click(object sender, RoutedEventArgs e) => GitHub?.RequestSettings();
}
