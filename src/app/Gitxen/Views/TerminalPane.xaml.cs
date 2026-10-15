// SPDX-License-Identifier: GPL-3.0-or-later
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text.Json;
using Gitxen.Services;
using Gitxen.Terminal;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Web.WebView2.Core;
using Windows.ApplicationModel.DataTransfer;

namespace Gitxen.Views;

/// <summary>
///  The bottom terminal: real shells — PowerShell, cmd, Git Bash, WSL — opened on the repository's
///  working directory, drawn by xterm.js.
/// </summary>
/// <remarks>
///  <para>
///   Each session is a shell on a ConPTY, so prompts, profiles and Oh My Posh behave as in any
///   console. Its output goes, unchanged, to an xterm.js surface in a WebView2 — the same renderer
///   VS Code embeds — which handles colours, the cursor and full-screen programs, and sends what is
///   typed straight back. The font and colours come from the user's Windows Terminal settings
///   (<see cref="WindowsTerminalLook"/>), and the toolbar can hand the folder to Windows Terminal
///   itself, which cannot be hosted inside another window.
///  </para>
///  <para>
///   The pane is shared by every repository tab, like the panel in an editor, so sessions survive
///   switching. Each session is pinned to the working directory it was opened on.
///  </para>
/// </remarks>
public sealed partial class TerminalPane : UserControl
{
    /// <summary>Until xterm.js has measured itself; it reports the real size as soon as it has.</summary>
    private const short InitialColumns = 120;
    private const short InitialRows = 30;

    private const string VirtualHost = "terminal.gitxen.local";

    private static Task<CoreWebView2Environment>? _environment;

    private readonly Dictionary<TerminalSession, Surface> _surfaces = [];
    private WindowsTerminalLook? _look;

    public TerminalPane()
    {
        InitializeComponent();
        Loaded += TerminalPane_Loaded;
    }

    /// <summary>The open sessions, in the order they were started.</summary>
    public ObservableCollection<TerminalSession> Sessions { get; } = [];

    /// <summary>Where a new session starts; the shell keeps this pointed at the active repository.</summary>
    public string WorkingDirectory { get; set; } = "";

    /// <summary>The user asked to hide the pane.</summary>
    public event EventHandler? CloseRequested;

    /// <summary>Starts the default shell if nothing is running yet. Called when the pane is shown.</summary>
    public void EnsureSession()
    {
        if (Sessions.Count == 0 && TerminalShells.Default is TerminalShell shell)
        {
            StartSession(shell);
        }
    }

    /// <summary>Puts the keyboard in the visible terminal.</summary>
    public void FocusTerminal()
    {
        if (Active is TerminalSession session && _surfaces.TryGetValue(session, out Surface? surface))
        {
            surface.Focus();
        }
    }

    /// <summary>Ends every session. The window calls this on close so no shell outlives the app.</summary>
    public void DisposeAll()
    {
        foreach (TerminalSession session in Sessions.ToList())
        {
            End(session);
        }

        Sessions.Clear();
    }

    private void TerminalPane_Loaded(object sender, RoutedEventArgs e)
    {
        if (ShellFlyout.Items.Count > 0)
        {
            return;
        }

        if (TerminalShells.Available.Count == 0)
        {
            ShellFlyout.Items.Add(new MenuFlyoutItem { Text = "No shells found", IsEnabled = false });
            return;
        }

        foreach (TerminalShell shell in TerminalShells.Available)
        {
            MenuFlyoutItem item = new() { Text = shell.Name };
            TerminalShell target = shell;
            item.Click += (_, _) => StartSession(target);
            ShellFlyout.Items.Add(item);
        }
    }

    private void StartSession(TerminalShell shell)
    {
        string workingDirectory = Directory.Exists(WorkingDirectory)
            ? WorkingDirectory
            : Environment.CurrentDirectory;

        TerminalSession session;
        try
        {
            session = new TerminalSession(shell, workingDirectory, DispatcherQueue, InitialColumns, InitialRows);
        }
        catch (Exception ex) when (ex is Win32Exception or IOException)
        {
            ShowStatus($"Could not start {shell.Name}: {ex.Message}");
            return;
        }

        _look ??= WindowsTerminalLook.Load();

        Surface surface = new(session, _look, this);
        _surfaces[session] = surface;
        Surfaces.Children.Add(surface.View);

        Sessions.Add(session);
        SessionPicker.SelectedItem = session;
        _ = surface.StartAsync();
    }

    private TerminalSession? Active => SessionPicker.SelectedItem as TerminalSession;

    private void SessionPicker_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        foreach ((TerminalSession session, Surface surface) in _surfaces)
        {
            surface.View.Visibility = ReferenceEquals(session, Active) ? Visibility.Visible : Visibility.Collapsed;
        }

        FocusTerminal();
    }

    private void OpenInWindowsTerminal_Click(object sender, RoutedEventArgs e)
    {
        string folder = Active?.WorkingDirectory ?? (Directory.Exists(WorkingDirectory) ? WorkingDirectory : "");

        if (folder.Length == 0 || ExternalTools.Available.FirstOrDefault(tool => tool.Id == "terminal") is not ExternalTool terminal)
        {
            ShowStatus("Windows Terminal was not found.");
            return;
        }

        string error = ExternalTools.Launch(terminal, folder);

        if (error.Length > 0)
        {
            ShowStatus(error);
        }
    }

    private void CloseSession_Click(object sender, RoutedEventArgs e)
    {
        if (Active is not TerminalSession session)
        {
            return;
        }

        End(session);
        Sessions.Remove(session);

        if (Sessions.Count > 0)
        {
            SessionPicker.SelectedItem = Sessions[^1];
        }
    }

    private void ClosePane_Click(object sender, RoutedEventArgs e) =>
        CloseRequested?.Invoke(this, EventArgs.Empty);

    private void End(TerminalSession session)
    {
        if (_surfaces.Remove(session, out Surface? surface))
        {
            Surfaces.Children.Remove(surface.View);
            surface.Dispose();
        }

        session.Dispose();
    }

    private void ShowStatus(string message)
    {
        StatusText.Text = message;
        StatusText.Visibility = Visibility.Visible;
    }

    /// <summary>
    ///  One WebView2 environment for every surface, with its data under %LOCALAPPDATA%\Gitxen: the
    ///  default is beside the executable, which an installed app cannot write to.
    /// </summary>
    private static Task<CoreWebView2Environment> SharedEnvironmentAsync() =>
        _environment ??= CoreWebView2Environment.CreateWithOptionsAsync(
            null,
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Gitxen", "WebView2"),
            new CoreWebView2EnvironmentOptions()).AsTask();

    /// <summary>A session's renderer: its WebView2, and the output held back until the page is ready.</summary>
    private sealed class Surface : IDisposable
    {
        private readonly TerminalSession _session;
        private readonly WindowsTerminalLook _look;
        private readonly TerminalPane _pane;
        private readonly List<string> _backlog = [];
        private bool _ready;

        public Surface(TerminalSession session, WindowsTerminalLook look, TerminalPane pane)
        {
            _session = session;
            _look = look;
            _pane = pane;

            View = new WebView2 { DefaultBackgroundColor = ParseColour(look.Background) };
            _session.Output += OnOutput;
        }

        public WebView2 View { get; }

        public async Task StartAsync()
        {
            try
            {
                await View.EnsureCoreWebView2Async(await SharedEnvironmentAsync());
            }
            catch (Exception ex) when (ex is COMException or FileNotFoundException or InvalidOperationException)
            {
                _pane.ShowStatus($"The terminal needs the Microsoft Edge WebView2 Runtime: {ex.Message}");
                return;
            }

            CoreWebView2 core = View.CoreWebView2;
            core.Settings.AreDevToolsEnabled = false;
            core.Settings.AreDefaultContextMenusEnabled = false;
            core.Settings.AreBrowserAcceleratorKeysEnabled = false;
            core.Settings.IsStatusBarEnabled = false;
            core.Settings.IsZoomControlEnabled = false;

            core.SetVirtualHostNameToFolderMapping(
                VirtualHost,
                Path.Combine(AppContext.BaseDirectory, "Assets", "Terminal"),
                CoreWebView2HostResourceAccessKind.DenyCors);

            // The page is local and fixed; nothing it does should take it anywhere else.
            core.NavigationStarting += (_, e) =>
                e.Cancel = !e.Uri.StartsWith($"https://{VirtualHost}/", StringComparison.OrdinalIgnoreCase);
            core.NewWindowRequested += (_, e) => e.Handled = true;
            core.WebMessageReceived += OnMessage;

            core.Navigate($"https://{VirtualHost}/index.html");
        }

        public void Focus()
        {
            View.Focus(FocusState.Programmatic);
            Post("""{"t":"focus"}""");
        }

        public void Dispose()
        {
            _session.Output -= OnOutput;
            View.Close();
        }

        private void OnOutput(string chunk)
        {
            if (_ready)
            {
                Post($$"""{"t":"out","d":{{JsonSerializer.Serialize(chunk)}}}""");
            }
            else
            {
                _backlog.Add(chunk);
            }
        }

        private void OnMessage(CoreWebView2 sender, CoreWebView2WebMessageReceivedEventArgs e)
        {
            if (!e.Source.StartsWith($"https://{VirtualHost}/", StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            using JsonDocument document = JsonDocument.Parse(e.WebMessageAsJson);
            JsonElement message = document.RootElement;

            switch (message.TryGetProperty("t", out JsonElement type) ? type.GetString() : "")
            {
                case "ready":
                    Post(InitMessage());
                    _ready = true;

                    foreach (string chunk in _backlog)
                    {
                        Post($$"""{"t":"out","d":{{JsonSerializer.Serialize(chunk)}}}""");
                    }

                    _backlog.Clear();
                    break;

                case "in":
                    _session.Write(message.GetProperty("d").GetString() ?? "");
                    break;

                case "size":
                    _session.Resize((short)message.GetProperty("cols").GetInt32(), (short)message.GetProperty("rows").GetInt32());
                    break;

                case "copy":
                    DataPackage package = new();
                    package.SetText(message.GetProperty("d").GetString() ?? "");
                    Clipboard.SetContent(package);
                    break;

                case "paste":
                    _ = PasteAsync();
                    break;

                case "hide":
                    _pane.CloseRequested?.Invoke(_pane, EventArgs.Empty);
                    break;
            }
        }

        private async Task PasteAsync()
        {
            DataPackageView content = Clipboard.GetContent();

            if (content.Contains(StandardDataFormats.Text))
            {
                string text = await content.GetTextAsync();
                Post($$"""{"t":"paste","d":{{JsonSerializer.Serialize(text)}}}""");
            }
        }

        private string InitMessage() =>
            $$"""{"t":"init","font":{{JsonSerializer.Serialize(_look.FontFamily)}},"fontSize":{{_look.FontSizeText}},"cursor":{{JsonSerializer.Serialize(_look.Cursor)}},"theme":{{JsonSerializer.Serialize(_look.Theme)}}}""";

        private void Post(string json)
        {
            if (View.CoreWebView2 is CoreWebView2 core)
            {
                core.PostWebMessageAsJson(json);
            }
        }

        private static Windows.UI.Color ParseColour(string hex)
        {
            if (hex.Length >= 7 && hex[0] == '#'
                && byte.TryParse(hex.AsSpan(1, 2), System.Globalization.NumberStyles.HexNumber, null, out byte red)
                && byte.TryParse(hex.AsSpan(3, 2), System.Globalization.NumberStyles.HexNumber, null, out byte green)
                && byte.TryParse(hex.AsSpan(5, 2), System.Globalization.NumberStyles.HexNumber, null, out byte blue))
            {
                return Windows.UI.Color.FromArgb(0xFF, red, green, blue);
            }

            return Colors.Black;
        }
    }
}
