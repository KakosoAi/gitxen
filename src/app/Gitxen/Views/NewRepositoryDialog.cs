// SPDX-License-Identifier: GPL-3.0-or-later
using Gitxen.GitHub;
using Gitxen.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Gitxen.Views;

/// <summary>
///  Asks for a new repository: where, what it is called, which branching workflow, and which starter
///  files — with GitHub's own .gitignore templates and licences when gh is available, and the option of
///  creating it on GitHub as well.
/// </summary>
/// <remarks>
///  The dialog opens at once and fills the two template lists when gh answers; without gh they stay at
///  "None" and publishing is unavailable, and everything else still works.
/// </remarks>
internal static class NewRepositoryDialog
{
    private const string NoneItem = "None";

    public static async Task<NewRepositoryChoices?> ShowAsync(XamlRoot root, string defaultParent, Func<Task<string?>> pickFolder)
    {
        TextBox name = new() { Header = "Name", PlaceholderText = "my-project" };
        TextBox location = new() { Header = "Location", Text = defaultParent };
        Button browse = new() { Content = "Browse…", VerticalAlignment = VerticalAlignment.Bottom };
        Grid locationRow = new() { ColumnSpacing = 8 };
        locationRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        locationRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        locationRow.Children.Add(location);
        locationRow.Children.Add(browse);
        Grid.SetColumn(browse, 1);

        TextBlock where = Note("");
        TextBox description = new() { Header = "Description (optional)", PlaceholderText = "One line about what it is" };

        TextBox branch = new() { Header = "Default branch", Text = "main", Width = 150 };
        ComboBox workflow = new()
        {
            Header = "Workflow",
            ItemsSource = new[] { RepositoryWorkflow.None, RepositoryWorkflow.GitHubFlow, RepositoryWorkflow.GitFlow, RepositoryWorkflow.TrunkBased }
                .Select(RepositoryTemplate.Title).ToList(),
            SelectedIndex = 1,
            MinWidth = 240
        };
        StackPanel branchRow = new() { Orientation = Orientation.Horizontal, Spacing = 12 };
        branchRow.Children.Add(branch);
        branchRow.Children.Add(workflow);
        TextBlock workflowNote = Note("");

        ComboBox gitignore = new() { Header = ".gitignore", ItemsSource = new List<string> { NoneItem }, SelectedIndex = 0, MinWidth = 220, IsEnabled = false };
        ComboBox license = new() { Header = "Licence", ItemsSource = new List<string> { NoneItem }, SelectedIndex = 0, MinWidth = 220, IsEnabled = false };
        StackPanel templateRow = new() { Orientation = Orientation.Horizontal, Spacing = 12 };
        templateRow.Children.Add(gitignore);
        templateRow.Children.Add(license);

        CheckBox readme = new() { Content = "README.md", IsChecked = true };
        CheckBox attributes = new() { Content = ".gitattributes (line endings)", IsChecked = true };
        StackPanel fileRow = new() { Orientation = Orientation.Horizontal, Spacing = 16 };
        fileRow.Children.Add(readme);
        fileRow.Children.Add(attributes);

        CheckBox publish = new() { Content = "Create it on GitHub too, and push", IsEnabled = false };
        RadioButtons visibility = new()
        {
            ItemsSource = new[] { "Private", "Public" },
            SelectedIndex = 0,
            MaxColumns = 2,
            Visibility = Visibility.Collapsed,
            Margin = new Thickness(28, -8, 0, 0)
        };
        TextBlock gitHubNote = Note("Checking for the GitHub CLI…");

        StackPanel panel = new() { Spacing = 12, Width = 500 };

        foreach (UIElement child in new UIElement[]
        {
            name, locationRow, where, description,
            Heading("Branches"), branchRow, workflowNote,
            Heading("Starter files"), templateRow, fileRow,
            Heading("GitHub"), publish, visibility, gitHubNote
        })
        {
            panel.Children.Add(child);
        }

        ContentDialog dialog = new()
        {
            Title = "New repository",
            Content = new ScrollViewer { Content = panel, MaxHeight = 560, Padding = new Thickness(0, 0, 16, 0) },
            PrimaryButtonText = "Create",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
            IsPrimaryButtonEnabled = false,
            XamlRoot = root
        };

        List<LicenseInfo> licenses = [];

        void Refresh()
        {
            string trimmed = name.Text.Trim();
            bool nameOk = trimmed.Length > 0 && trimmed.IndexOfAny(Path.GetInvalidFileNameChars()) < 0 && trimmed is not "." and not "..";
            bool branchOk = branch.Text.Trim().Length > 0 && !branch.Text.Trim().Contains(' ', StringComparison.Ordinal);
            bool locationOk = location.Text.Trim().Length > 0;

            where.Text = nameOk && locationOk ? Path.Combine(location.Text.Trim(), trimmed) : "";
            dialog.IsPrimaryButtonEnabled = nameOk && branchOk && locationOk;
            workflowNote.Text = RepositoryTemplate.Describe((RepositoryWorkflow)Math.Max(0, workflow.SelectedIndex));
            visibility.Visibility = publish.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        }

        name.TextChanged += (_, _) => Refresh();
        location.TextChanged += (_, _) => Refresh();
        branch.TextChanged += (_, _) => Refresh();
        workflow.SelectionChanged += (_, _) => Refresh();
        publish.Checked += (_, _) => Refresh();
        publish.Unchecked += (_, _) => Refresh();
        browse.Click += (_, _) => _ = BrowseAsync();
        dialog.Opened += (_, _) =>
        {
            Refresh();
            name.Focus(FocusState.Programmatic);
            _ = FillFromGitHubAsync();
        };

        async Task BrowseAsync()
        {
            // The folder picker is its own window; the dialog stays open behind it.
            if (await pickFolder() is string picked)
            {
                location.Text = picked;
            }
        }

        async Task FillFromGitHubAsync()
        {
            try
            {
                GhStatus status = await GitHubService.GetStatusAsync();

                if (!status.IsReady)
                {
                    gitHubNote.Text = "The GitHub CLI is not signed in, so there are no templates to choose from and the repository stays local. " +
                        "Sign in with `gh auth login` to have both.";
                    return;
                }

                gitHubNote.Text = $"Created under {status.Login}.";
                publish.IsEnabled = true;

                GitHubClient client = new(GitHubService.CreateCli(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)));
                Task<(IReadOnlyList<string> Items, GhResult Result)> templates = client.ListGitignoreTemplatesAsync();
                Task<(IReadOnlyList<LicenseInfo> Items, GhResult Result)> licenceList = client.ListLicensesAsync();

                (IReadOnlyList<string> templateNames, _) = await templates;
                gitignore.ItemsSource = new List<string> { NoneItem }.Concat(templateNames).ToList();
                gitignore.SelectedIndex = 0;
                gitignore.IsEnabled = templateNames.Count > 0;

                (IReadOnlyList<LicenseInfo> licenceItems, _) = await licenceList;
                licenses.AddRange(licenceItems);
                license.ItemsSource = new List<string> { NoneItem }.Concat(licenceItems.Select(l => l.Name)).ToList();
                license.SelectedIndex = 0;
                license.IsEnabled = licenceItems.Count > 0;
            }
            catch (Exception ex) when (ex is System.Text.Json.JsonException or InvalidOperationException)
            {
                // gh printed something else; the lists stay at None.
                SessionStore.LogCrash(ex);
            }
        }

        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
        {
            return null;
        }

        string gitignoreName = gitignore.SelectedItem as string ?? NoneItem;
        int licenseIndex = license.SelectedIndex - 1;

        return new NewRepositoryChoices(
            location.Text.Trim(),
            name.Text.Trim(),
            branch.Text.Trim(),
            (RepositoryWorkflow)Math.Max(0, workflow.SelectedIndex),
            description.Text.Trim(),
            gitignoreName == NoneItem ? "" : gitignoreName,
            licenseIndex >= 0 && licenseIndex < licenses.Count ? licenses[licenseIndex].Key : "",
            readme.IsChecked == true,
            attributes.IsChecked == true,
            publish.IsEnabled && publish.IsChecked == true,
            visibility.SelectedIndex != 1);
    }

    private static TextBlock Heading(string text) => new()
    {
        Text = text,
        Style = (Style)Application.Current.Resources["BodyStrongTextBlockStyle"],
        Margin = new Thickness(0, 8, 0, -4)
    };

    private static TextBlock Note(string text) => new()
    {
        Text = text,
        TextWrapping = TextWrapping.Wrap,
        Opacity = 0.7,
        Margin = new Thickness(0, -6, 0, 0)
    };
}
