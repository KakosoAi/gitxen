// SPDX-License-Identifier: GPL-3.0-or-later
using Microsoft.Win32;

namespace Gitxen.Services;

/// <summary>
///  What the installer left for the app to read.
/// </summary>
internal static class InstallInfo
{
    private const string Key = @"Software\Kakoso\Gitxen";

    /// <summary>
    ///  A value the installer writes on a fresh install only — not on an upgrade — and that differs
    ///  every time: the installation's product code. Empty for a portable copy or a development build.
    /// </summary>
    /// <remarks>
    ///  The first-run wizard is keyed to it. A session file outlives an uninstall, because it lives in
    ///  the person's own profile, so "never set up" alone missed everyone who installed Gitxen again;
    ///  and keying it to the version would have put the wizard in front of every upgrade.
    /// </remarks>
    public static string FirstRunToken
    {
        get
        {
            try
            {
                using RegistryKey? key = Registry.LocalMachine.OpenSubKey(Key);
                return key?.GetValue("FirstRunToken") as string ?? "";
            }
            catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
            {
                return "";
            }
        }
    }
}
