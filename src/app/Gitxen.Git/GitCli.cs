// SPDX-License-Identifier: GPL-3.0-or-later
using System.Diagnostics;
using System.Text;

namespace Gitxen.Git;

/// <summary>
///  Runs git.
/// </summary>
/// <remarks>
///  <para>
///   The whole mechanism of a git client: launch <c>git.exe</c> as a child process, hand it
///   arguments, read what it prints. Nothing about git is implemented here or anywhere else in this
///   application — git does the work, and this is the part that asks it to.
///  </para>
///  <para>
///   That also settles a licensing question. Invoking a program is not linking to it, so running
///   git — which is itself GPL-2.0 — places no obligation on the caller.
///  </para>
///  <para>
///   Both a blocking and an awaitable form are offered. The blocking one is not sync-over-async —
///   it reads the streams on this thread and borrows one worker for standard error — because the
///   callers that want it are already inside <c>Task.Run</c>, and giving them a real synchronous
///   path is simpler than making every caller async at once.
///  </para>
/// </remarks>
public sealed class GitCli
{
    /// <summary>
    ///  Options applied before the subcommand, to every read.
    /// </summary>
    /// <remarks>
    ///  <c>--no-optional-locks</c> keeps read-only commands from taking <c>index.lock</c> to write a
    ///  refreshed index back. Two commands that both do that can race, and one of them simply fails.
    ///  Nothing asked to read is ever asked to write, so there is no reason to take the lock.
    /// </remarks>
    private static readonly string[] _readOnlyPrefix = ["--no-optional-locks"];

    private static readonly string[] _noPrefix = [];

    private readonly string _executable;
    private readonly string _workingDirectory;

    /// <param name="workingDirectory">The repository to run in.</param>
    /// <param name="executable">
    ///  How to find git. The default relies on PATH, which is what a normal install provides.
    /// </param>
    public GitCli(string workingDirectory, string executable = "git")
    {
        _workingDirectory = workingDirectory;
        _executable = executable;
    }

    /// <summary>Raised as each command finishes, for the activity console.</summary>
    public event EventHandler<GitCommandCompleted>? CommandCompleted;

    /// <summary>Runs a command that only reads, so it may take the no-locks prefix.</summary>
    public GitResult Read(GitArgs args) => Run(args, _readOnlyPrefix, CancellationToken.None);

    /// <summary>
    ///  Runs a command that only reads, and stops git if <paramref name="cancellationToken"/> fires.
    /// </summary>
    /// <remarks>
    ///  For the listings that are read synchronously on a background thread but can be superseded — a
    ///  commit's changed files, when the selection has already moved on.
    /// </remarks>
    /// <exception cref="OperationCanceledException">The token fired; the process has been killed.</exception>
    public GitResult Read(GitArgs args, CancellationToken cancellationToken) =>
        Run(args, _readOnlyPrefix, cancellationToken);

    /// <summary>
    ///  Runs a read-only command whose output is a stream of NUL-terminated records, handing them over
    ///  as git produces them rather than once it has finished.
    /// </summary>
    /// <param name="onBatch">
    ///  Called with the records that arrived together — one read from the pipe, which is as many as
    ///  git had flushed. Batching by arrival keeps the first results quick and the rest efficient
    ///  without any tuning: a slow command delivers small batches promptly, a fast one large ones.
    ///  Return <c>false</c> to stop early; git is killed.
    /// </param>
    /// <returns>
    ///  The exit code and standard error. Standard output is empty: it went to <paramref name="onBatch"/>.
    /// </returns>
    /// <exception cref="OperationCanceledException">The token fired; the process has been killed.</exception>
    public GitResult ReadRecords(GitArgs args, Func<IReadOnlyList<string>, bool> onBatch, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(onBatch);
        cancellationToken.ThrowIfCancellationRequested();

        using Process process = Create(args, _readOnlyPrefix);

        long started = Stopwatch.GetTimestamp();
        process.Start();
        process.StandardInput.Close();

        using CancellationTokenRegistration stop = cancellationToken.Register(() => TryKill(process));

        byte[] errorBytes = [];
        Thread errorReader = new(() => errorBytes = ReadAll(process.StandardError.BaseStream))
        {
            IsBackground = true,
            Name = "git stderr"
        };

        errorReader.Start();

        if (!ReadBatches(process.StandardOutput.BaseStream, onBatch))
        {
            // The consumer has what it wanted. Killing git is what stops it writing into a pipe
            // nobody is reading any more.
            TryKill(process);
        }

        process.WaitForExit();
        errorReader.Join();

        cancellationToken.ThrowIfCancellationRequested();

        return Complete(args, process.ExitCode, [], errorBytes, started);
    }

    /// <summary>Runs a command that changes the repository.</summary>
    public GitResult Write(GitArgs args) => Run(args, _noPrefix, CancellationToken.None);

    /// <summary>Runs a command that only reads, so it may take the no-locks prefix.</summary>
    public Task<GitResult> ReadAsync(GitArgs args, CancellationToken cancellationToken = default) =>
        RunAsync(args, _readOnlyPrefix, cancellationToken);

    /// <summary>Runs a command that changes the repository.</summary>
    public Task<GitResult> WriteAsync(GitArgs args, CancellationToken cancellationToken = default) =>
        RunAsync(args, _noPrefix, cancellationToken);

    private GitResult Run(GitArgs args, string[] prefix, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        using Process process = Create(args, prefix);

        long started = Stopwatch.GetTimestamp();
        process.Start();
        process.StandardInput.Close();

        // Killing git closes both pipes, which is what unblocks the reads below.
        using CancellationTokenRegistration stop = cancellationToken.Register(() => TryKill(process));

        // Standard error is drained on its own thread while this one drains standard output.
        // Reading either to completion before starting the other deadlocks the moment the unread
        // pipe fills, which a large diff manages easily.
        //
        // A plain thread rather than a task: this path is synchronous by design, and Join gives the
        // happens-before that publishes the bytes without any waiting-on-a-task to reason about.
        byte[] errorBytes = [];
        Thread errorReader = new(() => errorBytes = ReadAll(process.StandardError.BaseStream))
        {
            IsBackground = true,
            Name = "git stderr"
        };

        errorReader.Start();
        byte[] output = ReadAll(process.StandardOutput.BaseStream);

        process.WaitForExit();
        errorReader.Join();

        // Whatever a killed git managed to write is a partial answer, and must not pass for a whole one.
        cancellationToken.ThrowIfCancellationRequested();

        return Complete(args, process.ExitCode, output, errorBytes, started);
    }

    /// <summary>
    ///  Runs a command that changes the repository and reports its progress as it goes: clone, fetch,
    ///  pull, push, given <c>--progress</c> so git reports even though its standard error is a pipe.
    ///  Blocks; for a background thread.
    /// </summary>
    /// <remarks>
    ///  Standard error is read as it arrives and split on carriage returns as well as newlines — git
    ///  redraws a progress line with <c>\r</c> — and each piece that <see cref="GitProgressParser"/>
    ///  recognises goes to <paramref name="onProgress"/>, on the reading thread. The rest of standard
    ///  error is returned in the result, without the progress lines, so a message built from it reads as
    ///  it would in a terminal. Cancelling kills git and its children (the transport helper doing the
    ///  network work) and throws <see cref="OperationCanceledException"/>.
    /// </remarks>
    public GitResult WriteWithProgress(GitArgs args, Action<GitProgress> onProgress, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(onProgress);
        cancellationToken.ThrowIfCancellationRequested();

        using Process process = Create(args, _noPrefix);

        long started = Stopwatch.GetTimestamp();
        process.Start();
        process.StandardInput.Close();

        using CancellationTokenRegistration stop = cancellationToken.Register(() => TryKill(process));

        byte[] output = [];
        Thread outputReader = new(() => output = ReadAll(process.StandardOutput.BaseStream))
        {
            IsBackground = true,
            Name = "git stdout"
        };

        outputReader.Start();
        byte[] error = ReadProgress(process.StandardError.BaseStream, onProgress);

        process.WaitForExit();
        outputReader.Join();
        cancellationToken.ThrowIfCancellationRequested();

        return Complete(args, process.ExitCode, output, error, started);
    }

    /// <summary><see cref="WriteWithProgress"/> off the calling thread.</summary>
    public Task<GitResult> WriteWithProgressAsync(GitArgs args, Action<GitProgress> onProgress, CancellationToken cancellationToken = default) =>
        Task.Run(() => WriteWithProgress(args, onProgress, cancellationToken), CancellationToken.None);

    /// <summary>Reports each progress line as it arrives; returns standard error with those lines taken out.</summary>
    private static byte[] ReadProgress(Stream stream, Action<GitProgress> onProgress)
    {
        System.Text.Decoder decoder = System.Text.Encoding.UTF8.GetDecoder();
        System.Text.StringBuilder kept = new();
        System.Text.StringBuilder line = new();
        byte[] buffer = new byte[4096];
        char[] characters = new char[System.Text.Encoding.UTF8.GetMaxCharCount(buffer.Length)];

        void Flush(char ending)
        {
            string text = line.ToString();
            line.Clear();

            if (GitProgressParser.Parse(text) is GitProgress progress)
            {
                onProgress(progress);
            }
            else if (text.Length > 0 || ending == '\n')
            {
                kept.Append(text).Append('\n');
            }
        }

        try
        {
            int read;

            while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
            {
                int count = decoder.GetChars(buffer, 0, read, characters, 0);

                for (int i = 0; i < count; i++)
                {
                    if (characters[i] is '\r' or '\n')
                    {
                        Flush(characters[i]);
                    }
                    else
                    {
                        line.Append(characters[i]);
                    }
                }
            }

            if (line.Length > 0)
            {
                Flush('\n');
            }
        }
        catch (IOException)
        {
            // Killed mid-write: what arrived is still returned.
        }

        return System.Text.Encoding.UTF8.GetBytes(kept.ToString());
    }

    private async Task<GitResult> RunAsync(GitArgs args, string[] prefix, CancellationToken cancellationToken)
    {
        using Process process = Create(args, prefix);

        long started = Stopwatch.GetTimestamp();
        process.Start();
        process.StandardInput.Close();

        Task<byte[]> output = ReadAllAsync(process.StandardOutput.BaseStream, cancellationToken);
        Task<byte[]> error = ReadAllAsync(process.StandardError.BaseStream, cancellationToken);

        try
        {
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // The caller has stopped caring, so neither should git. Without this the process keeps
            // running and keeps writing into a pipe nobody is draining.
            TryKill(process);
            throw;
        }

        return Complete(
            args,
            process.ExitCode,
            await output.ConfigureAwait(false),
            await error.ConfigureAwait(false),
            started);
    }

    private Process Create(GitArgs args, string[] prefix)
    {
        ArgumentNullException.ThrowIfNull(args);

        Process process = new()
        {
            StartInfo =
            {
                FileName = _executable,
                WorkingDirectory = _workingDirectory,

                // Required for redirection, and it keeps a console window from flashing up.
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,

                // Nothing here writes to git's stdin, and leaving it attached to ours would let a
                // command that decides to prompt block forever instead of failing.
                RedirectStandardInput = true
            }
        };

        foreach (string value in prefix)
        {
            process.StartInfo.ArgumentList.Add(value);
        }

        foreach (string value in args.Config.Concat(args.Values))
        {
            process.StartInfo.ArgumentList.Add(value);
        }

        foreach ((string name, string value) in args.Environment)
        {
            process.StartInfo.Environment[name] = value;
        }

        return process;
    }

    private GitResult Complete(GitArgs args, int exitCode, byte[] output, byte[] error, long started)
    {
        CommandCompleted?.Invoke(this, new GitCommandCompleted(
            $"{_executable} {args}",
            _workingDirectory,
            exitCode,
            Stopwatch.GetElapsedTime(started)));

        return new GitResult(exitCode, output, error);
    }

    private static byte[] ReadAll(Stream stream)
    {
        using MemoryStream buffer = new();

        try
        {
            stream.CopyTo(buffer);
        }
        catch (IOException)
        {
            // The process died mid-write. Whatever arrived before that is still worth returning:
            // the exit code is what decides whether it means anything.
        }

        return buffer.ToArray();
    }

    /// <summary>
    ///  Reads NUL-terminated records off <paramref name="stream"/>, one batch per read.
    /// </summary>
    /// <returns><c>false</c> if the consumer asked to stop.</returns>
    private static bool ReadBatches(Stream stream, Func<IReadOnlyList<string>, bool> onBatch)
    {
        // A record can straddle two reads, so the unfinished tail of one read is kept at the front of
        // the buffer for the next.
        byte[] buffer = new byte[128 * 1024];
        int length = 0;

        try
        {
            while (true)
            {
                if (buffer.Length - length < 32 * 1024)
                {
                    // One record larger than the buffer: a commit message the size of a small novel.
                    Array.Resize(ref buffer, buffer.Length * 2);
                }

                int read = stream.Read(buffer, length, buffer.Length - length);

                if (read == 0)
                {
                    break;
                }

                length += read;

                List<string> batch = [];
                int start = 0;

                while (buffer.AsSpan(start, length - start).IndexOf((byte)0) is int nul and >= 0)
                {
                    batch.Add(Encoding.UTF8.GetString(buffer, start, nul));
                    start += nul + 1;
                }

                if (start > 0)
                {
                    Buffer.BlockCopy(buffer, start, buffer, 0, length - start);
                    length -= start;
                }

                if (batch.Count > 0 && !onBatch(batch))
                {
                    return false;
                }
            }
        }
        catch (IOException)
        {
            // The process was killed mid-write. Whatever arrived before that has been delivered.
            return true;
        }

        // Output that ends without a terminator is still a record.
        if (length > 0)
        {
            return onBatch([Encoding.UTF8.GetString(buffer, 0, length)]);
        }

        return true;
    }

    private static async Task<byte[]> ReadAllAsync(Stream stream, CancellationToken cancellationToken)
    {
        using MemoryStream buffer = new();

        try
        {
            await stream.CopyToAsync(buffer, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is OperationCanceledException or IOException)
        {
            // Cancelled, or the process died mid-write. See above.
        }

        return buffer.ToArray();
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or NotSupportedException or SystemException)
        {
            // Already gone, or the platform refused. Either way there is nothing further to do.
        }
    }
}

/// <summary>One finished git invocation, for the activity console.</summary>
public sealed record GitCommandCompleted(string CommandLine, string WorkingDirectory, int ExitCode, TimeSpan Duration);
