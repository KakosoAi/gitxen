// SPDX-License-Identifier: GPL-3.0-or-later
namespace Gitxen.Services;

/// <summary>
///  The folder Gitxen was asked to open when it was started — from Explorer's "Open with Gitxen", or
///  from <c>gitxen &lt;folder&gt;</c> on a command line.
/// </summary>
internal static class LaunchRequest
{
    /// <summary>
    ///  The first argument of a command line, as a full path, or null when there is none.
    /// </summary>
    /// <param name="commandLine">The command line as Windows passed it.</param>
    /// <param name="startsWithExecutable">
    ///  Whether the first token is the program itself, as it is in <see cref="Environment.CommandLine"/>.
    ///  An activation redirected from a second copy carries its arguments in the same form, and one
    ///  whose first token ends in <c>.exe</c> is treated the same way.
    /// </param>
    /// <remarks>
    ///  Split by hand rather than with the C runtime's rules, which treat a backslash before a quote as
    ///  an escape: Explorer passes a drive as <c>"D:\"</c>, and those rules read that as <c>D:"</c>.
    ///  Here a quote only ever opens or closes a quoted token.
    /// </remarks>
    public static string? FolderFrom(string commandLine, bool startsWithExecutable)
    {
        List<string> tokens = Split(commandLine);

        if (tokens.Count > 0 && (startsWithExecutable || tokens[0].EndsWith(".exe", StringComparison.OrdinalIgnoreCase)))
        {
            tokens.RemoveAt(0);
        }

        string? argument = tokens.FirstOrDefault(token => token.Length > 0 && token[0] != '-');

        if (argument is null)
        {
            return null;
        }

        try
        {
            string full = Path.GetFullPath(argument);

            // A file opens the repository it belongs to.
            return File.Exists(full) ? Path.GetDirectoryName(full) : full;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
    }

    /// <summary>
    ///  The top of the working tree <paramref name="folder"/> is in: the folder itself or the nearest
    ///  one above it that is a repository. Null when none is.
    /// </summary>
    /// <remarks>
    ///  Explorer's menu appears on every folder, and right-clicking inside <c>src\app</c> of a
    ///  repository means that repository, not "no repository here".
    /// </remarks>
    public static string? FindRepositoryRoot(string folder)
    {
        DirectoryInfo? current;

        try
        {
            current = new DirectoryInfo(folder);
        }
        catch (Exception ex) when (ex is ArgumentException or PathTooLongException)
        {
            return null;
        }

        while (current is not null)
        {
            if (GitDirectory.IsRepository(current.FullName))
            {
                return current.FullName;
            }

            current = current.Parent;
        }

        return null;
    }

    private static List<string> Split(string commandLine)
    {
        List<string> tokens = [];
        System.Text.StringBuilder token = new();
        bool quoted = false;
        bool any = false;

        foreach (char character in commandLine)
        {
            if (character == '"')
            {
                quoted = !quoted;
                any = true;
            }
            else if (char.IsWhiteSpace(character) && !quoted)
            {
                if (any)
                {
                    tokens.Add(token.ToString());
                    token.Clear();
                    any = false;
                }
            }
            else
            {
                token.Append(character);
                any = true;
            }
        }

        if (any)
        {
            tokens.Add(token.ToString());
        }

        return tokens;
    }
}
