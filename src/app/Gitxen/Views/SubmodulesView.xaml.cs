// SPDX-License-Identifier: GPL-3.0-or-later
using Gitxen.Models;
using Gitxen.ViewModels;
using Microsoft.UI.Xaml;
using Windows.ApplicationModel.DataTransfer;

namespace Gitxen.Views;

/// <summary>Submodules and their checkout state, with per-submodule updating.</summary>
public sealed partial class SubmodulesView : RepositoryPage
{
    public SubmodulesView()
    {
        InitializeComponent();
    }

    public override async Task ActivateAsync()
    {
        if (Tab is RepositoryTabViewModel tab)
        {
            await tab.LoadSubmodulesAsync();
        }
    }

    private static SubmoduleInfo? SubmoduleFrom(object sender) =>
        (sender as FrameworkElement)?.DataContext as SubmoduleInfo;

    private async void Refresh_Click(object sender, RoutedEventArgs e) => await RefreshAsync();

    private async void UpdateAll_Click(object sender, RoutedEventArgs e)
    {
        await ReportAsync(tab => tab.UpdateSubmodulesAsync());
        await ActivateAsync();
    }

    private async void Sync_Click(object sender, RoutedEventArgs e)
    {
        await ReportAsync(tab => tab.SyncSubmodulesAsync());
        await ActivateAsync();
    }

    private async void Update_Click(object sender, RoutedEventArgs e)
    {
        if (SubmoduleFrom(sender) is SubmoduleInfo submodule)
        {
            await ReportAsync(tab => tab.UpdateSubmoduleAsync(submodule.Path));
            await ActivateAsync();
        }
    }

    private void CopyPath_Click(object sender, RoutedEventArgs e)
    {
        if (SubmoduleFrom(sender) is not SubmoduleInfo submodule || Tab is not RepositoryTabViewModel tab)
        {
            return;
        }

        DataPackage package = new();
        package.SetText(submodule.Path);
        Clipboard.SetContent(package);

        tab.ReportInformation("Copied", $"{submodule.Path} copied to the clipboard.");
    }
}
