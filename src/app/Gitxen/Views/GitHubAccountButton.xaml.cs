// SPDX-License-Identifier: GPL-3.0-or-later
using Gitxen.GitHub;
using Gitxen.Services;
using Gitxen.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Gitxen.Views;

/// <summary>
///  The account a repository's GitHub pages act as, with a menu of the other accounts gh is signed in
///  to. Shared by Pull requests, Issues and Actions, which each reload when the account changes.
/// </summary>
public sealed partial class GitHubAccountButton : UserControl
{
    public static readonly DependencyProperty ModelProperty = DependencyProperty.Register(
        nameof(Model), typeof(GitHubRepositoryViewModel), typeof(GitHubAccountButton), new PropertyMetadata(null));

    public GitHubAccountButton()
    {
        InitializeComponent();
    }

    public GitHubRepositoryViewModel? Model
    {
        get => (GitHubRepositoryViewModel?)GetValue(ModelProperty);
        set => SetValue(ModelProperty, value);
    }

    /// <summary>Raised once the repository has been reached as the chosen account, so the page can read again.</summary>
    public event EventHandler? AccountChanged;

    /// <summary>Built each time it opens, so an account signed in since is offered without a restart.</summary>
    private void AccountMenu_Opening(object? sender, object e)
    {
        AccountMenu.Items.Clear();

        if (Model is not GitHubRepositoryViewModel model || GitHubService.Current is not GhStatus { IsReady: true } status)
        {
            return;
        }

        AccountMenu.Items.Add(new MenuFlyoutItem
        {
            Text = "Pushes, pulls and GitHub pages all use the account checked here.",
            IsEnabled = false
        });

        foreach (string login in status.Accounts)
        {
            ToggleMenuFlyoutItem item = new()
            {
                Text = login == status.Login ? $"{login}  (gh's active account)" : login,
                IsChecked = login == model.Account,
                Tag = login
            };

            item.Click += Account_Click;
            AccountMenu.Items.Add(item);
        }

        AccountMenu.Items.Add(new MenuFlyoutSeparator());

        MenuFlyoutItem automatic = new()
        {
            Text = "Choose automatically",
            Tag = ""
        };

        ToolTipService.SetToolTip(automatic, "Use gh's active account, or the first signed-in account that can read this repository.");
        automatic.Click += Account_Click;
        AccountMenu.Items.Add(automatic);

        if (model.Account.Length > 0)
        {
            AccountMenu.Items.Add(new MenuFlyoutSeparator());

            MenuFlyoutItem commitAs = new()
            {
                Text = $"Commit here as {model.Account}",
                Icon = new FontIcon { Glyph = "\uE77B" }
            };

            ToolTipService.SetToolTip(commitAs, "Set this repository's commit name and email to the account's, with GitHub's no-reply address.");
            commitAs.Click += CommitAs_Click;
            AccountMenu.Items.Add(commitAs);
        }
    }

    private async void CommitAs_Click(object sender, RoutedEventArgs e)
    {
        if (Model is GitHubRepositoryViewModel model)
        {
            model.Tab.Report(await model.CommitAsAccountAsync());
        }
    }

    private async void Account_Click(object sender, RoutedEventArgs e)
    {
        if (Model is GitHubRepositoryViewModel model && sender is FrameworkElement { Tag: string login })
        {
            await model.UseAccountAsync(login);
            AccountChanged?.Invoke(this, EventArgs.Empty);
        }
    }
}
