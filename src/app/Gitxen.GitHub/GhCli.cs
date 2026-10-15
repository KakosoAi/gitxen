// SPDX-License-Identifier: GPL-3.0-or-later
using System.Diagnostics;
using System.Text;

namespace Gitxen.GitHub;

/// <summary>What one <c>gh</c> invocation said.</summary>
public sealed class GhResult
{
    internal GhResult(int exitCode, string output, string error, bool launched)
    {
        ExitCode = exitCode;
        Output = output;
        Error = error;
        WasLaunched = launched;
    }

    public int ExitCode { get; }

    /// <summary>Standard output, decoded as UTF-8, with the trailing newline removed.</summary>
    public string Output { get; }

    /// <summary>Standard error, decoded as UTF-8, with the trailing newline removed.</summary>
    public string Error { get; }

    /// <summary>
    ///  False when the process could not be started at all — <c>gh</c> is not installed, or not where
    ///  it was said to be. Distinct from a non-zero exit, which means gh ran and declined.
    /// </summary>
    public bool WasLaunched { get; }

    public bool Succeeded => WasLaunched && ExitCode == 0;

    /// <summary>Whichever stream has something to say: output on success, otherwise the error.</summary>
    public string Message => Succeeded || Error.Length == 0 ? Output : Error;

    /// <summary>Both streams together, for showing a person what happened.</summary>
    public string AllOutput =>
        Output.Length == 0 ? Error
        : Error.Length == 0 ? Output
        : Output + "\n" + Error;
}

/// <summary>One finished <c>gh</c> invocation, for the activity console.</summary>
public sealed record GhCommandCompleted(string CommandLine, string WorkingDirectory, int ExitCode, TimeSpan Duration);

/// <summary>
///  Runs the GitHub CLI.
/// </summary>
/// <remarks>
///  <para>
///   The same idea as <c>GitCli</c> and the same reasons: arguments go through
///   <c>ArgumentList</c> so nothing is quoted by hand, a non-zero exit is data rather than an
///   exception, and every command is reported so the activity console can show it.
///  </para>
///  <para>
///   <b>gh is what holds the credential, not this.</b> It is signed in once, by the person, with
///   <c>gh auth login</c>, and keeps its token in the Windows credential store. Nothing here ever
///   sees it, asks for it or stores it — which is the point of going through gh rather than the API.
///  </para>
///  <para>
///   The environment is set so gh behaves as a tool rather than as a person's terminal: prompts off
///   (a command that decides to ask a question would otherwise wait for an answer nothing can give),
///   no colour codes in output that will be parsed, no update notices, no spinner.
///  </para>
/// </remarks>
public sealed class GhCli
{
    /// <summary>
    ///  How long a command may run before it is stopped. gh talks to the network, and a hung
    ///  connection would otherwise leave a page loading for ever.
    /// </summary>
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(90);

    private readonly string _executable;
    private readonly string _workingDirectory;

    /// <param name="workingDirectory">The repository to run in; gh works out the rest from its remotes.</param>
    /// <param name="executable">How to find gh. The default relies on PATH, which is what its installer provides.</param>
    public GhCli(string workingDirectory, string executable = "gh")
    {
        _workingDirectory = workingDirectory;
        _executable = string.IsNullOrWhiteSpace(executable) ? "gh" : executable;
    }

    public event EventHandler<GhCommandCompleted>? CommandCompleted;

    /// <summary>
    ///  A token for gh to act with instead of its active account, passed to each command as
    ///  <c>GH_TOKEN</c>, which gh documents as taking precedence over the accounts it has stored.
    /// </summary>
    /// <remarks>
    ///  How one repository uses a different signed-in account from another: gh has one active account
    ///  per host, and switching it would change it for every other program too. The token comes from
    ///  gh itself (<see cref="GitHubClient.GetAccountTokenAsync"/>) and goes straight back to it; it is
    ///  kept in this object only, never written anywhere, logged or shown. The activity console records
    ///  the arguments, and the token is not one of them.
    /// </remarks>
    public string? Token { get; set; }

    /// <summary>Runs gh and waits for it.</summary>
    /// <param name="standardInput">
    ///  Text to feed gh, for the commands that read a body from <c>--body-file -</c>. Passed this way
    ///  rather than as an argument so a long comment cannot hit the command-line length limit and no
    ///  quoting question arises for text that is arbitrary by nature.
    /// </param>
    public async Task<GhResult> RunAsync(
        IEnumerable<string> args,
        string? standardInput = null,
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(args);

        List<string> arguments = [.. args];

        Process process = new()
        {
            StartInfo =
            {
                FileName = _executable,
                WorkingDirectory = Directory.Exists(_workingDirectory) ? _workingDirectory : Environment.CurrentDirectory,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                RedirectStandardInput = true,

                // All three explicit. Left alone, standard input is written in the console's code page
                // rather than UTF-8, so an accented letter or a check mark in a comment reaches gh as
                // bytes that are not valid UTF-8 and the request is rejected — or, worse, is accepted
                // and stored as something else. No byte-order mark either: gh would send it as text.
                StandardInputEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8
            }
        };

        foreach (string argument in arguments)
        {
            process.StartInfo.ArgumentList.Add(argument);
        }

        process.StartInfo.Environment["GH_PROMPT_DISABLED"] = "1";
        process.StartInfo.Environment["NO_COLOR"] = "1";
        process.StartInfo.Environment["GH_NO_UPDATE_NOTIFIER"] = "1";
        process.StartInfo.Environment["GH_SPINNER_DISABLED"] = "1";

        if (!string.IsNullOrEmpty(Token))
        {
            process.StartInfo.Environment["GH_TOKEN"] = Token;
        }

        long started = Stopwatch.GetTimestamp();

        try
        {
            process.Start();
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            process.Dispose();
            return Complete(arguments, new GhResult(-1, "", ex.Message, launched: false), started);
        }

        using (process)
        {
            using CancellationTokenSource limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            limit.CancelAfter(timeout ?? DefaultTimeout);

            Task<string> output = process.StandardOutput.ReadToEndAsync(limit.Token);
            Task<string> error = process.StandardError.ReadToEndAsync(limit.Token);

            try
            {
                // Written and closed before waiting, or a command reading stdin would wait for ever.
                if (standardInput is not null)
                {
                    await process.StandardInput.WriteAsync(standardInput.AsMemory(), limit.Token).ConfigureAwait(false);
                }

                process.StandardInput.Close();

                await process.WaitForExitAsync(limit.Token).ConfigureAwait(false);

                return Complete(
                    arguments,
                    new GhResult(
                        process.ExitCode,
                        Trim(await output.ConfigureAwait(false)),
                        Trim(await error.ConfigureAwait(false)),
                        launched: true),
                    started);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                // The timeout, not the caller: report it as a result rather than throwing, since a slow
                // network is something to tell a person about, not an error in the program.
                Kill(process);

                return Complete(
                    arguments,
                    new GhResult(-2, "", $"gh did not answer within {(timeout ?? DefaultTimeout).TotalSeconds:0} seconds.", launched: true),
                    started);
            }
            catch (OperationCanceledException)
            {
                Kill(process);
                throw;
            }
        }
    }

    private GhResult Complete(List<string> arguments, GhResult result, long started)
    {
        CommandCompleted?.Invoke(this, new GhCommandCompleted(
            $"{_executable} {string.Join(' ', arguments.Select(Display))}",
            _workingDirectory,
            result.ExitCode,
            Stopwatch.GetElapsedTime(started)));

        return result;
    }

    /// <summary>For the console only: an argument with a space in it is shown quoted so the line reads.</summary>
    private static string Display(string argument) =>
        argument.Length == 0 || argument.Any(char.IsWhiteSpace) ? $"\"{argument.ReplaceLineEndings(" ")}\"" : argument;

    private static string Trim(string text) => text.TrimEnd('\n', '\r');

    private static void Kill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or NotSupportedException or System.ComponentModel.Win32Exception)
        {
            // Already gone, or refused. Either way there is nothing further to do.
        }
    }
}
