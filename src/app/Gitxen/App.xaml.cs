// SPDX-License-Identifier: GPL-3.0-or-later
using Gitxen.Services;
using Gitxen.Theming;
using Gitxen.Views;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;

namespace Gitxen;

public partial class App : Application
{
    public App()
    {
        InitializeComponent();

        UnhandledException += (_, e) => SessionStore.LogCrash(e.Exception);
    }

    /// <summary>
    ///  The shell window.
    /// </summary>
    /// <remarks>
    ///  Static because the file and folder pickers need an owning window handle, and a desktop WinUI
    ///  app has no ambient one to fall back on — the pages that show a picker are several levels below
    ///  the window and have no other route to it. This app only ever creates one window.
    /// </remarks>
    public static Window? Shell { get; private set; }

    /// <summary>Set once the session has been restored, after which a redirected folder can open at once.</summary>
    private bool _shellReady;

    /// <summary>A folder handed over by a second start before this one was ready for it.</summary>
    private string? _pendingFolder;

    protected override async void OnLaunched(LaunchActivatedEventArgs args)
    {
        // Before anything is shown: a second start hands its folder to the copy already running and
        // leaves, rather than playing the splash and restoring the session a second time.
        if (await SingleInstance.RedirectIfRunningAsync())
        {
            Exit();
            return;
        }

        string? requested = LaunchRequest.FolderFrom(Environment.CommandLine, startsWithExecutable: true);

        DispatcherQueue queue = DispatcherQueue.GetForCurrentThread();
        SingleInstance.Redirected += folder => queue.TryEnqueue(() => OnRedirected(folder));

        // Read the session and settle the theme before the window is constructed. The theme brushes
        // become entries on Application.Resources, and every page resolves its ThemeResource
        // references against them as it is parsed -- a window built first would come up in the WinUI
        // default colours and then visibly change once the saved theme was applied.
        SessionState state = SessionStore.Load();
        AppOptions.Apply(state);

        // Before any window: pages resolve the dense-row style as they are parsed.
        DensityService.Apply();
        ThemeService.Initialize();

        // The splash plays before the shell is built, not alongside it. There is one UI thread, and
        // constructing MainWindow parses every page in the application — perhaps a second of solid
        // work during which nothing can be drawn, so an animation started first would simply freeze.
        // Playing it out first costs that second in sequence and is the only way it is ever seen.
        SplashWindow splash = new();
        splash.Show();
        await splash.PlayIntroAsync();

        MainWindow window = new() { OpenOnLauncher = requested is null };
        Shell = window;

        // Awaited rather than fired and forgotten: the shell is shown only once the splash has
        // finished leaving, so the two are never on screen together.
        await splash.FinishAsync();

        window.Activate();

        _ = StartAsync(window, state, requested);
    }

    /// <summary>Restores the session, then opens the folder Gitxen was started with, if any.</summary>
    private async Task StartAsync(MainWindow window, SessionState state, string? requested)
    {
        await window.RestoreSessionAsync(state);

        _shellReady = true;

        string? folder = requested ?? _pendingFolder;
        _pendingFolder = null;

        if (folder is not null)
        {
            await window.OpenRequestedAsync(folder);
        }
    }

    /// <summary>Another start of Gitxen handed over to this one: come to the front, and open its folder.</summary>
    private void OnRedirected(string? folder)
    {
        if (!_shellReady || Shell is not MainWindow window)
        {
            _pendingFolder = folder ?? _pendingFolder;
            return;
        }

        window.BringToFront();

        if (folder is not null)
        {
            _ = window.OpenRequestedAsync(folder);
        }
    }
}
