// SPDX-License-Identifier: GPL-3.0-or-later
using System.Text;
using Microsoft.UI.Dispatching;

namespace Gitxen.Terminal;

/// <summary>
///  One running shell in the terminal pane: the process and the raw VT stream it prints.
/// </summary>
/// <remarks>
///  The PTY reports output on its reader thread; this is the layer that owns the locking and hands the
///  stream to the UI thread. Output is coalesced — a build spraying output posts one batch at a time to
///  the renderer, not one message per pipe read. Rendering itself is xterm.js's job.
/// </remarks>
public sealed class TerminalSession : ObservableObject, IDisposable
{
    private readonly ConPtySession _pty;
    private readonly DispatcherQueue _dispatcher;
    private readonly Lock _pendingLock = new();
    private readonly StringBuilder _pending = new();
    private int _flushQueued;
    private bool _isRunning = true;

    public TerminalShell Shell { get; }

    /// <summary>What the session picker shows: the shell and the folder it was opened on.</summary>
    public string Title { get; }

    public string WorkingDirectory { get; }

    public TerminalSession(TerminalShell shell, string workingDirectory, DispatcherQueue dispatcher, short columns, short rows)
    {
        Shell = shell;
        WorkingDirectory = workingDirectory;
        _dispatcher = dispatcher;

        string folder = Path.GetFileName(Path.TrimEndingDirectorySeparator(workingDirectory));
        Title = folder.Length > 0 ? $"{shell.Name} — {folder}" : shell.Name;

        _pty = new ConPtySession(shell.ExecutablePath, shell.Arguments, workingDirectory, columns, rows);
        _pty.OutputReceived += OnOutputReceived;
        _pty.Exited += OnExited;
    }

    /// <summary>Raw VT output, in batches, on the UI thread.</summary>
    public event Action<string>? Output;

    public bool IsRunning
    {
        get => _isRunning;
        private set => SetProperty(ref _isRunning, value);
    }

    /// <summary>Keystrokes from the renderer, exactly as typed.</summary>
    public void Write(string input) => _pty.Write(input);

    public void Resize(short columns, short rows)
    {
        if (IsRunning)
        {
            _pty.Resize(columns, rows);
        }
    }

    public void Dispose() => _pty.Dispose();

    private void OnOutputReceived(string chunk)
    {
        lock (_pendingLock)
        {
            _pending.Append(chunk);
        }

        ScheduleFlush();
    }

    private void OnExited(int exitCode)
    {
        OnOutputReceived($"\r\n\u001b[90m[{Shell.Name} exited with code {exitCode}]\u001b[0m\r\n");
        _dispatcher.TryEnqueue(() => IsRunning = false);
    }

    /// <summary>
    ///  Puts one flush on the UI queue, unless one is already waiting — the flag is what stops a
    ///  chatty build enqueueing thousands of posts.
    /// </summary>
    private void ScheduleFlush()
    {
        if (Interlocked.CompareExchange(ref _flushQueued, 1, 0) != 0)
        {
            return;
        }

        _dispatcher.TryEnqueue(() =>
        {
            Interlocked.Exchange(ref _flushQueued, 0);

            string batch;
            lock (_pendingLock)
            {
                batch = _pending.ToString();
                _pending.Clear();
            }

            if (batch.Length > 0)
            {
                Output?.Invoke(batch);
            }
        });
    }
}
