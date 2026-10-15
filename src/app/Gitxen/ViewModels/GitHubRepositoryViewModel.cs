// SPDX-License-Identifier: GPL-3.0-or-later
using Gitxen.GitHub;
using Gitxen.Models;
using Gitxen.Services;
using Microsoft.UI.Xaml;

namespace Gitxen.ViewModels;

/// <summary>Whether the GitHub sections can show anything, and if not, why not.</summary>
public enum GitHubGate
{
    Checking,
    NotInstalled,
    NotSignedIn,

    /// <summary>gh works, but this repository cannot be reached: not on GitHub, or no access to it.</summary>
    NoAccess,

    Ready
}

/// <summary>
///  One repository's GitHub state: whether it can be reached, and the three sections built on it.
/// </summary>
/// <remarks>
///  <para>
///   Held by the tab rather than by each page so the answer to "can we talk to GitHub" is worked out
///   once per repository, and so Pull requests, Issues and Actions — which all need it — agree.
///  </para>
///  <para>
///   Nothing here runs until a GitHub section is opened. A repository on GitHub costs nothing until
///   somebody looks at it, and one that is not on GitHub never shows the sections at all.
///  </para>
/// </remarks>
public sealed class GitHubRepositoryViewModel : ObservableObject, IDisposable
{
    private readonly SemaphoreSlim _readyLock = new(1, 1);

    /// <summary>
    ///  Set when the settings or the remote have changed since the answer was worked out. The old
    ///  answer keeps showing until somebody next asks, at which point it is checked again.
    /// </summary>
    private bool _stale;

    private GitHubGate _gate = GitHubGate.Checking;
    private string _gateTitle = "";
    private string _gateMessage = "";
    private GitHubRepository? _repository;
    private GitHubClient? _client;
    private string _account = "";

    public GitHubRepositoryViewModel(RepositoryTabViewModel tab)
    {
        Tab = tab;
        PullRequests = new GitHubPullRequestsViewModel(this);
        Issues = new GitHubIssuesViewModel(this);
        Actions = new GitHubActionsViewModel(this);
        CommitChecks = new GitHubCommitChecksViewModel(this);

        GitHubService.SettingsChanged += Reset;
    }

    /// <summary>Stops listening and polling; called when the tab closes.</summary>
    public void Dispose()
    {
        GitHubService.SettingsChanged -= Reset;
        Actions.Stop();
        CommitChecks.Dispose();
        _readyLock.Dispose();
    }

    public RepositoryTabViewModel Tab { get; }

    public GitHubPullRequestsViewModel PullRequests { get; }

    public GitHubIssuesViewModel Issues { get; }

    public GitHubActionsViewModel Actions { get; }

    /// <summary>The CI result for each commit the History page lists.</summary>
    public GitHubCommitChecksViewModel CommitChecks { get; }

    /// <summary>Asks for the application's Settings page, where gh is pointed at and signed in.</summary>
    public event EventHandler? SettingsRequested;

    public void RequestSettings() => SettingsRequested?.Invoke(this, EventArgs.Empty);

    // ---- Whether the sections are offered at all ----

    /// <summary>
    ///  The three navigation entries show for a repository on github.com while the integration is on.
    /// </summary>
    /// <remarks>
    ///  Deliberately not dependent on gh being installed: someone who has not installed it is exactly
    ///  who should find these entries and be told what to do, and hiding them would leave no way to
    ///  learn the feature exists.
    /// </remarks>
    public Visibility NavVisibility =>
        AppOptions.GitHubEnabled && Tab.Host is { IsGitHub: true } ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>Called when the remote, or a setting, changes.</summary>
    /// <remarks>
    ///  The navigation entries follow at once; what a page is showing is re-checked the next time it
    ///  is opened, rather than being blanked under someone who is reading it.
    /// </remarks>
    public void Reset()
    {
        _stale = true;
        OnPropertyChanged(nameof(NavVisibility));
        OnPropertyChanged(nameof(RepositoryName));
        CommitChecks.Invalidate();
    }

    // ---- The gate ----

    public GitHubGate Gate
    {
        get => _gate;
        private set
        {
            if (SetProperty(ref _gate, value))
            {
                OnPropertyChanged(nameof(GateVisibility));
                OnPropertyChanged(nameof(ContentVisibility));
                OnPropertyChanged(nameof(CheckingVisibility));
                OnPropertyChanged(nameof(GateActionsVisibility));
                OnPropertyChanged(nameof(GetGhVisibility));
            }
        }
    }

    public string GateTitle
    {
        get => _gateTitle;
        private set => SetProperty(ref _gateTitle, value);
    }

    public string GateMessage
    {
        get => _gateMessage;
        private set => SetProperty(ref _gateMessage, value);
    }

    /// <summary>The explanation shows instead of the page whenever GitHub cannot be reached.</summary>
    public Visibility GateVisibility =>
        Gate is GitHubGate.Ready or GitHubGate.Checking ? Visibility.Collapsed : Visibility.Visible;

    public Visibility ContentVisibility => Gate == GitHubGate.Ready ? Visibility.Visible : Visibility.Collapsed;

    public Visibility CheckingVisibility => Gate == GitHubGate.Checking ? Visibility.Visible : Visibility.Collapsed;

    public Visibility GateActionsVisibility => Gate is GitHubGate.NotInstalled or GitHubGate.NotSignedIn ? Visibility.Visible : Visibility.Collapsed;

    public Visibility GetGhVisibility => Gate == GitHubGate.NotInstalled ? Visibility.Visible : Visibility.Collapsed;

    private void SetGate(GitHubGate gate, string title, string message)
    {
        GateTitle = title;
        GateMessage = message;
        Gate = gate;
    }

    // ---- The repository ----

    public GitHubRepository? Repository
    {
        get => _repository;
        private set
        {
            if (SetProperty(ref _repository, value))
            {
                OnPropertyChanged(nameof(RepositoryName));
                OnPropertyChanged(nameof(CanWrite));
            }
        }
    }

    public string RepositoryName => Repository?.NameWithOwner ?? HostName;

    /// <summary><c>owner/name</c> from the remote, which is what the account choice is keyed by.</summary>
    private string HostName => Tab.Host is null ? "" : $"{Tab.Host.Owner}/{Tab.Host.Name}";

    /// <summary>The signed-in account this repository is being read as.</summary>
    public string Account
    {
        get => _account;
        private set
        {
            if (SetProperty(ref _account, value))
            {
                OnPropertyChanged(nameof(AccountText));
            }
        }
    }

    public string AccountText => Account.Length == 0 ? "" : $"as {Account}";

    /// <summary>
    ///  Reads this repository as a particular signed-in account from now on, or — given an empty login
    ///  — goes back to choosing one automatically. The page reloads itself afterwards.
    /// </summary>
    /// <remarks>
    ///  Pushes and pulls from Gitxen follow the choice at once. For git run anywhere else — a terminal,
    ///  an editor — the repository's own config names the account to Git Credential Manager, which asks
    ///  for that account's sign-in once if it does not hold it yet. Only this repository's config is
    ///  touched.
    /// </remarks>
    public async Task UseAccountAsync(string login)
    {
        AppOptions.SetGitHubAccount(HostName, login);
        Tab.ForgetGitHubCredential();
        await Tab.SetConfigValueAsync(GitConfigScope.Local, GitHubAccountChoice.CredentialUserKey, login);
        _stale = true;
        CommitChecks.Invalidate();
        await EnsureReadyAsync(force: true);
    }

    /// <summary>
    ///  Commits in this repository from now on carry the account's name and its GitHub no-reply address,
    ///  so they link to the profile without publishing a real address. Only this repository's config.
    /// </summary>
    public async Task<GitOperationResult> CommitAsAccountAsync()
    {
        const string Description = "Commit identity";

        if (_client is null || await _client.GetAccountAsync() is not GitHubAccount account)
        {
            return new GitOperationResult(Description, false, "Could not read the account from GitHub.");
        }

        string name = account.Name.Length > 0 ? account.Name : account.Login;
        GitOperationResult named = await Tab.SetConfigValueAsync(GitConfigScope.Local, "user.name", name);

        if (!named.Succeeded)
        {
            return named;
        }

        GitOperationResult mailed = await Tab.SetConfigValueAsync(GitConfigScope.Local, "user.email", account.NoReplyEmail);

        return mailed.Succeeded
            ? new GitOperationResult(Description, true, $"Commits here are now made as {name} <{account.NoReplyEmail}>.")
            : mailed;
    }

    /// <summary>Whether the signed-in account can merge, close and re-run here, which decides which buttons are offered.</summary>
    public bool CanWrite => Repository?.CanWrite ?? false;

    /// <summary>The branch to open a pull request into by default.</summary>
    public string DefaultBranch => Repository?.DefaultBranch is { Length: > 0 } branch ? branch : "main";

    /// <summary>The runner for this repository. Only meaningful once <see cref="EnsureReadyAsync"/> has succeeded.</summary>
    internal GitHubClient Client => _client ?? throw new InvalidOperationException("GitHub is not ready.");

    /// <summary>
    ///  Works out whether GitHub can be used for this repository, once, and remembers the answer.
    /// </summary>
    /// <param name="force">Ask again from the beginning — after signing in, or installing gh.</param>
    /// <returns>True when the sections can go ahead and read.</returns>
    public async Task<bool> EnsureReadyAsync(bool force = false)
    {
        if (!force && Gate == GitHubGate.Ready && !_stale)
        {
            return true;
        }

        await _readyLock.WaitAsync();

        try
        {
            // Another caller may have finished the same work while this one waited for the lock.
            if (!force && Gate == GitHubGate.Ready && !_stale)
            {
                return true;
            }

            _stale = false;
            SetGate(GitHubGate.Checking, "", "");

            HostedRepository? host = Tab.Host;

            if (!AppOptions.GitHubEnabled || host is not { IsGitHub: true })
            {
                SetGate(GitHubGate.NoAccess, "Not a GitHub repository", "This repository's remote is not on github.com, or GitHub integration is switched off in Settings.");
                return false;
            }

            GhStatus status = await GitHubService.GetStatusAsync(force);

            if (status.Availability == GhAvailability.NotInstalled)
            {
                SetGate(
                    GitHubGate.NotInstalled,
                    "The GitHub CLI was not found",
                    "Gitxen talks to GitHub through the GitHub CLI (gh), which keeps its own sign-in. Install it, then choose Check again. If it is installed somewhere unusual, say where in Settings.");
                return false;
            }

            if (status.Availability == GhAvailability.NotSignedIn)
            {
                SetGate(
                    GitHubGate.NotSignedIn,
                    "gh is not signed in",
                    "The GitHub CLI is installed but has no account for github.com. Sign in from Settings, or run `gh auth login` in a terminal, then choose Check again.");
                return false;
            }

            (GitHubClient? client, GitHubRepository? repository, string account, GhResult? failure) = await ConnectAsync(status, $"{host.Owner}/{host.Name}");

            if (client is null || repository is null)
            {
                SetGate(
                    GitHubGate.NoAccess,
                    "Cannot reach this repository on GitHub",
                    failure is not null && !GitHubClient.IsNoAccess(failure) && failure.Message.Length > 0
                        ? failure.Message
                        : $"{host.Owner}/{host.Name} could not be read by any account gh is signed in to ({string.Join(", ", status.Accounts)}). "
                            + "It may be private and not shared with them. Sign in to another account with `gh auth login`, then choose Check again.");
                return false;
            }

            _client = client;
            Account = account;
            Repository = repository;
            SetGate(GitHubGate.Ready, "", "");
            return true;
        }
        finally
        {
            _readyLock.Release();
        }
    }

    /// <summary>
    ///  Finds a signed-in account that can read the repository, and a client acting as it.
    /// </summary>
    /// <remarks>
    ///  <para>
    ///   The account chosen for this repository goes first, then gh's active account, then the rest in
    ///   gh's order. gh has one active account per host, and people with a work and a personal account
    ///   — or two organisations — have repositories each can see and the other cannot; switching gh's
    ///   active account to suit one repository would break the others, and every other program's too.
    ///  </para>
    ///  <para>
    ///   Moving on is only for "cannot see it". Any other failure — the network, GitHub itself — is
    ///   reported as it is, because asking again as someone else would only repeat it more slowly.
    ///  </para>
    /// </remarks>
    private async Task<(GitHubClient? Client, GitHubRepository? Repository, string Account, GhResult? Failure)> ConnectAsync(GhStatus status, string name)
    {
        string chosen = await Tab.GetChosenGitHubAccountAsync();

        List<string> order = [];

        if (chosen.Length > 0 && status.Accounts.Contains(chosen))
        {
            order.Add(chosen);
        }

        foreach (string login in (IEnumerable<string>)[status.Login, .. status.Accounts])
        {
            if (login.Length > 0 && !order.Contains(login))
            {
                order.Add(login);
            }
        }

        GhResult? failure = null;

        foreach (string login in order)
        {
            GhCli cli = GitHubService.CreateCli(Tab.WorkingDir);

            // gh's active account needs nothing extra; any other acts through its own token.
            if (login != status.Login)
            {
                if (await GitHubClient.GetAccountTokenAsync(GitHubService.CreateCli(Tab.WorkingDir), login) is not string token)
                {
                    continue;
                }

                cli.Token = token;
            }

            GitHubClient client = new(cli, name);
            (GitHubRepository? repository, GhResult result) = await client.GetRepositoryAsync();

            if (repository is not null)
            {
                // Remembered when it took a search to find, so the next visit goes straight to it.
                if (login != order[0])
                {
                    AppOptions.SetGitHubAccount(name, login);
                }

                return (client, repository, login, result);
            }

            failure = result;

            if (!GitHubClient.IsNoAccess(result))
            {
                break;
            }
        }

        return (null, null, "", failure);
    }

    /// <summary>The Check again button: start over, and load the section that asked.</summary>
    public async Task RecheckAsync()
    {
        GitHubService.Invalidate();
        await EnsureReadyAsync(force: true);
    }

    // ---- Operations ----

    /// <summary>
    ///  Runs one gh command and turns what it said into something the repository's result bar can show.
    ///  Never throws: a refusal from GitHub is information for the person, not an error in the program.
    /// </summary>
    internal async Task<GitOperationResult> RunAsync(string description, Func<GitHubClient, Task<GhResult>> operation)
    {
        try
        {
            if (!await EnsureReadyAsync())
            {
                return new GitOperationResult(description, false, GateMessage);
            }

            GhResult result = await operation(Client);

            return new GitOperationResult(description, result.Succeeded, result.AllOutput.Trim());
        }
        catch (OperationCanceledException)
        {
            return new GitOperationResult(description, false, "Cancelled.");
        }
        catch (Exception ex)
        {
            return new GitOperationResult(description, false, ex.Message);
        }
    }

    /// <summary>
    ///  Runs a gh command whose output is the answer rather than a message — a diff, a log — and hands
    ///  back the raw result. Null when GitHub cannot be reached at all.
    /// </summary>
    internal async Task<GhResult?> RunRawAsync(Func<GitHubClient, Task<GhResult>> operation)
    {
        try
        {
            return await EnsureReadyAsync() ? await operation(Client) : null;
        }
        catch (OperationCanceledException)
        {
            return null;
        }
    }

    /// <summary>Puts a result in the repository's result bar.</summary>
    public void Report(GitOperationResult result) => Tab.Report(result);

    /// <summary>
    ///  Re-reads the repository after an operation that changed it — a checkout changes the branch,
    ///  the working tree and the history all at once.
    /// </summary>
    public Task RefreshRepositoryAsync()
    {
        Tab.InvalidateListings();
        return Tab.LoadAsync();
    }
}
