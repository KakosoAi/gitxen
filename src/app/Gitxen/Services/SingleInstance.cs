// SPDX-License-Identifier: GPL-3.0-or-later
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Windows.AppLifecycle;
using Windows.ApplicationModel.Activation;

namespace Gitxen.Services;

/// <summary>
///  Keeps one Gitxen window per installation: a second start hands its folder to the copy already
///  running and exits.
/// </summary>
/// <remarks>
///  <para>
///   Without this, "Open with Gitxen" on three folders opened three windows, each restoring and
///   saving the same session file over the others' changes.
///  </para>
///  <para>
///   The key includes the folder the executable runs from, so an installed copy and a development
///   build are separate: a build being tested never hands its work to the installed version, which
///   may not understand it.
///  </para>
/// </remarks>
internal static class SingleInstance
{
    /// <summary>
    ///  Raised in the copy that stays, on a background thread, when another start was redirected to
    ///  it. Carries the folder that start was asked to open, or null for a plain start.
    /// </summary>
    public static event Action<string?>? Redirected;

    /// <summary>
    ///  Hands this start to the copy already running, if there is one.
    /// </summary>
    /// <returns>True when it was handed over and this copy should exit.</returns>
    public static async Task<bool> RedirectIfRunningAsync()
    {
        AppInstance main = AppInstance.FindOrRegisterForKey(Key);

        if (main.IsCurrent)
        {
            main.Activated += OnActivated;
            return false;
        }

        // Lets the running copy bring its window to the front; Windows otherwise only allows that to
        // the process the person last interacted with, which is this one.
        _ = AllowSetForegroundWindow(main.ProcessId);

        await main.RedirectActivationToAsync(AppInstance.GetCurrent().GetActivatedEventArgs());
        return true;
    }

    private static string Key
    {
        get
        {
            byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(AppContext.BaseDirectory.ToUpperInvariant()));
            return $"Gitxen-{Convert.ToHexString(hash)[..16]}";
        }
    }

    private static void OnActivated(object? sender, AppActivationArguments args)
    {
        string? folder = args.Kind == ExtendedActivationKind.Launch && args.Data is ILaunchActivatedEventArgs launch
            ? LaunchRequest.FolderFrom(launch.Arguments ?? "", startsWithExecutable: false)
            : null;

        Redirected?.Invoke(folder);
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AllowSetForegroundWindow(uint processId);
}
