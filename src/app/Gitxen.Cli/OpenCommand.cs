// SPDX-License-Identifier: GPL-3.0-or-later
using System.Diagnostics;

namespace Gitxen.Cli;

/// <summary><c>gx open [path]</c>: the repository in the Gitxen window, which takes it over if it is already running.</summary>
internal static class OpenCommand
{
    public static int Run(Arguments arguments)
    {
        string root = Program.RepositoryRoot(arguments[0]);
        string window = Path.Combine(AppContext.BaseDirectory, "Gitxen.exe");

        if (!File.Exists(window))
        {
            Output.Error($"Gitxen.exe is not next to gx ({AppContext.BaseDirectory}). Install Gitxen, or run the gx that came with it.");
            return 1;
        }

        using Process? started = Process.Start(new ProcessStartInfo(window) { ArgumentList = { root }, UseShellExecute = false });
        return 0;
    }
}
