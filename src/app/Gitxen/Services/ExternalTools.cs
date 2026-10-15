// SPDX-License-Identifier: GPL-3.0-or-later
using System.Diagnostics;

namespace Gitxen.Services;

/// <summary>
///  An application that can be opened on a repository's working directory.
/// </summary>
/// <param name="Id">Stable identifier, used by the command palette.</param>
/// <param name="Name">What the menu says.</param>
/// <param name="Glyph">Segoe Fluent glyph for the menu item.</param>
/// <param name="Executable">The command to look for on PATH, without an extension.</param>
/// <param name="TerminalCommand">
///  For a command-line tool, the command to run inside a terminal. Empty for applications that take
///  the folder as an argument directly.
/// </param>
public sealed record ExternalTool(
    string Id,
    string Name,
    string Glyph,
    string Executable,
    string TerminalCommand = "");

/// <summary>
///  Opens a repository in another application — an editor, a terminal, or a coding agent.
/// </summary>
/// <remarks>
///  <para>
///   Availability is decided by probing PATH rather than by trying to launch and seeing what happens:
///   a menu that lists everything and fails on click is worse than one that lists what is installed.
///   The probe is a directory scan of the PATH entries, so it costs no processes.
///  </para>
///  <para>
///   Command-line tools are launched through Windows Terminal where it exists, because they need a
///   console to run in and Terminal is the one that can be told which directory to start in. Without
///   it they fall back to the classic console host.
///  </para>
/// </remarks>
public static class ExternalTools
{
    /// <summary>Extensions a command can have on Windows, in the order the shell would resolve them.</summary>
    private static readonly string[] _executableExtensions = [".exe", ".cmd", ".bat", ".com"];

    private static readonly ExternalTool[] _all =
    [
        new("vscode", "Visual Studio Code", "", "code"),
        new("terminal", "Windows Terminal", "", "wt"),
        new("claude", "Claude Code", "", "claude", TerminalCommand: "claude"),
        new("codex", "Codex", "", "codex", TerminalCommand: "codex"),
        new("explorer", "File Explorer", "", "explorer")
    ];

    /// <summary>The tools that are actually installed, in menu order.</summary>
    public static IReadOnlyList<ExternalTool> Available =>
        [.. _all.Where(tool => Resolve(tool.Executable) is not null)];

    /// <summary>
    ///  Opens <paramref name="workingDirectory"/> in <paramref name="tool"/>.
    /// </summary>
    /// <returns>An error message, or empty when the application was started.</returns>
    public static string Launch(ExternalTool tool, string workingDirectory)
    {
        if (Resolve(tool.Executable) is not string executable)
        {
            return $"{tool.Name} was not found on your PATH.";
        }

        try
        {
            ProcessStartInfo start = tool.TerminalCommand.Length == 0
                ? DirectStart(executable, workingDirectory)
                : TerminalStart(tool.TerminalCommand, workingDirectory);

            // UseShellExecute so .cmd shims — which is how most of these install — resolve properly.
            start.UseShellExecute = true;
            start.WorkingDirectory = workingDirectory;

            Process.Start(start);
            return "";
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return ex.Message;
        }
    }

    private static ProcessStartInfo DirectStart(string executable, string workingDirectory) =>
        new(executable, [workingDirectory]);

    /// <summary>
    ///  Runs a console tool in a terminal opened at the repository.
    /// </summary>
    /// <remarks>
    ///  Windows Terminal is preferred because <c>-d</c> sets the starting directory; the fallback runs
    ///  through cmd with <c>/k</c> so the window stays open if the tool exits immediately, which is
    ///  otherwise indistinguishable from nothing having happened.
    /// </remarks>
    private static ProcessStartInfo TerminalStart(string command, string workingDirectory) =>
        Resolve("wt") is not null
            ? new ProcessStartInfo("wt", ["-d", workingDirectory, command])
            : new ProcessStartInfo("cmd", ["/k", command]);

    /// <summary>
    ///  Runs a command from the repository in a terminal window of its own, opened at the repository,
    ///  which stays open afterwards so its output can be read.
    /// </summary>
    /// <returns>An error message, or empty when the terminal was started.</returns>
    /// <remarks>
    ///  Windows Terminal when it is installed, titled with the command; otherwise a console window of
    ///  the shell itself. PowerShell 7 is preferred to Windows PowerShell, and scripts ending in .sh run
    ///  in the bash that Git for Windows installs.
    /// </remarks>
    public static string RunInTerminal(RunCommand command, string workingDirectory)
    {
        ArgumentNullException.ThrowIfNull(command);

        List<string> shell;

        switch (command.Shell)
        {
            case RunShell.Bash:
                if (FindBash() is not string bash)
                {
                    return "bash was not found. It comes with Git for Windows.";
                }

                shell = [bash, "-c", $"{command.Command}; exec bash"];
                break;

            case RunShell.Cmd:
                shell = ["cmd", "/k", command.Command];
                break;

            default:
                shell = [Resolve("pwsh") ?? "powershell", "-NoExit", "-Command", command.Command];
                break;
        }

        try
        {
            ProcessStartInfo start;

            if (Resolve("wt") is not null)
            {
                // Windows Terminal reads a semicolon as the start of another tab's command line, even
                // inside quotes, unless it is escaped.
                start = new ProcessStartInfo("wt");
                start.ArgumentList.Add("-d");
                start.ArgumentList.Add(workingDirectory);
                start.ArgumentList.Add("--title");
                start.ArgumentList.Add(command.Title);

                foreach (string part in shell)
                {
                    start.ArgumentList.Add(part.Replace(";", "\\;", StringComparison.Ordinal));
                }
            }
            else
            {
                start = new ProcessStartInfo(shell[0]);

                foreach (string part in shell.Skip(1))
                {
                    start.ArgumentList.Add(part);
                }
            }

            start.UseShellExecute = true;
            start.WorkingDirectory = workingDirectory;
            Process.Start(start);
            return "";
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return ex.Message;
        }
    }

    /// <summary>Git for Windows' bash, found next to git or on PATH.</summary>
    private static string? FindBash()
    {
        foreach (string candidate in new[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Git", "bin", "bash.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "Git", "bin", "bash.exe")
        })
        {
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return Resolve("bash") is not null ? "bash" : null;
    }

    /// <summary>
    ///  Finds a command on PATH, returning the name to launch or null when it is not installed.
    /// </summary>
    private static string? Resolve(string command)
    {
        // Shipped with Windows; probing PATH for it is pointless and occasionally wrong.
        if (command == "explorer")
        {
            return command;
        }

        string path = Environment.GetEnvironmentVariable("PATH") ?? "";

        foreach (string directory in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            foreach (string extension in _executableExtensions)
            {
                try
                {
                    if (File.Exists(Path.Combine(directory.Trim(), command + extension)))
                    {
                        return command;
                    }
                }
                catch (ArgumentException)
                {
                    // A malformed PATH entry is not worth failing the whole probe over.
                }
            }
        }

        return null;
    }
}
