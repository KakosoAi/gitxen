// SPDX-License-Identifier: GPL-3.0-or-later
using System.Runtime.InteropServices;
using Gitxen.Theming;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media.Animation;
using Windows.Graphics;
using WinRT.Interop;

namespace Gitxen.Views;

/// <summary>
///  The window shown while the application starts: the branch illustration drawing itself, then the
///  wordmark.
/// </summary>
/// <remarks>
///  <para>
///   A WinUI window rather than the packaged-app splash image, because this app is unpackaged — and
///   because a still image could not draw itself. It carries no border, no title bar and no entry in
///   the task bar, so it reads as a splash rather than as a window that happens to be small.
///  </para>
///  <para>
///   It never delays the launch. <see cref="FinishAsync"/> lets the intro reach its end if it is
///   still running, and startup continues behind it either way; on a machine where the shell comes
///   up in 200ms the splash is gone almost at once, which is the correct behaviour rather than a
///   missed opportunity to show it off.
///  </para>
/// </remarks>
public sealed partial class SplashWindow : Window
{
    private const int SplashWidth = 520;
    private const int SplashHeight = 300;

    /// <summary>
    ///  How long the two storyboards in the markup run for.
    /// </summary>
    /// <remarks>
    ///  Mirrored here rather than read from the storyboards' own Completed events: those are raised
    ///  on the UI thread this method is already waiting on, and a storyboard belonging to a window
    ///  that never gets composed would never raise one at all. Timing it out is the same wait with
    ///  one less way to hang. Keep these in step with the markup.
    /// </remarks>
    private static readonly TimeSpan _introDuration = TimeSpan.FromMilliseconds(1250);

    private static readonly TimeSpan _outroDuration = TimeSpan.FromMilliseconds(260);

    private readonly DateTime _shownAt = DateTime.UtcNow;

    public SplashWindow()
    {
        InitializeComponent();

        // The palette is already resolved by the time this is constructed, so the splash comes up in
        // the user's colours rather than flashing the WinUI default at them. Set directly rather
        // than through ThemeService.Attach, which claims the one window the service follows — that
        // is the shell's, and a splash must not take it.
        Root.RequestedTheme = ThemeService.Current.IsDark ? ElementTheme.Dark : ElementTheme.Light;

        if (AppWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.SetBorderAndTitleBar(false, false);
            presenter.IsResizable = false;
            presenter.IsAlwaysOnTop = true;
        }

        AppWindow.IsShownInSwitchers = false;
        AppWindow.Title = "Gitxen";

        CentreOnPrimaryDisplay();
    }

    /// <summary>Shows the window and starts the intro.</summary>
    public void Show()
    {
        Activate();

        if (Root.Resources["Intro"] is Storyboard intro)
        {
            intro.Begin();
        }
    }

    /// <summary>
    ///  Waits for the intro to play, leaving the UI thread free to draw it.
    /// </summary>
    /// <remarks>
    ///  The caller must await this before doing anything substantial on the UI thread. An animation
    ///  only advances while frames are being produced, and nothing is produced while that thread is
    ///  inside a constructor.
    /// </remarks>
    public async Task PlayIntroAsync()
    {
        TimeSpan remaining = _introDuration - (DateTime.UtcNow - _shownAt);

        if (remaining > TimeSpan.Zero)
        {
            await Task.Delay(remaining);
        }
    }

    /// <summary>
    ///  Lets the intro finish, plays the outro, and closes.
    /// </summary>
    /// <remarks>
    ///  Any of the intro still outstanding is waited out first, so it is never cut off mid-draw.
    /// </remarks>
    public async Task FinishAsync()
    {
        await PlayIntroAsync();

        if (Root.Resources["Outro"] is Storyboard outro)
        {
            outro.Begin();
            await Task.Delay(_outroDuration);
        }

        Close();
    }

    /// <summary>
    ///  Puts the splash in the middle of the display the mouse is on, in that display's own pixels.
    /// </summary>
    /// <remarks>
    ///  <c>AppWindow</c> works in physical pixels while the XAML inside it works in effective ones,
    ///  so the window has to be resized by the display's scaling or it comes up too small on a
    ///  high-DPI screen with the content clipped.
    /// </remarks>
    private void CentreOnPrimaryDisplay()
    {
        DisplayArea display = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Primary);

        // XamlRoot has no scale yet — nothing has been composed — so the display's own DPI is what
        // there is to go on. 96 is the unscaled baseline Windows counts from.
        double scale = GetDpiForWindow(WindowNative.GetWindowHandle(this)) / 96.0;

        int width = (int)Math.Round(SplashWidth * scale);
        int height = (int)Math.Round(SplashHeight * scale);

        RectInt32 work = display.WorkArea;

        AppWindow.MoveAndResize(new RectInt32(
            work.X + ((work.Width - width) / 2),
            work.Y + ((work.Height - height) / 2),
            width,
            height));
    }

    /// <summary>
    ///  "Version 0.2.0", from the assembly, so the splash cannot disagree with the build.
    /// </summary>
    /// <remarks>An instance property because x:Bind resolves against the page, not against a type.</remarks>
    public string VersionText
    {
        get
        {
            Version? version = typeof(SplashWindow).Assembly.GetName().Version;
            return version is null ? "" : $"Version {version.ToString(3)}";
        }
    }

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hwnd);
}
