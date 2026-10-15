// SPDX-License-Identifier: GPL-3.0-or-later
using Gitxen.Git;

namespace Gitxen.Services;

/// <summary>
///  Creates repositories: cloning one from a URL, initialising an empty one, or starting one from a
///  <see cref="RepositoryTemplate"/>.
/// </summary>
/// <remarks>
///  Separate from <c>RepositoryLoader</c>, which is built around a repository that already
///  exists. These two commands run in the <em>parent</em> directory and produce the working directory
///  the rest of the app then opens, so there is nothing for a per-repository loader to attach to.
/// </remarks>
internal sealed class RepositoryCreator
{
    /// <summary>Told about every git command run, for the app's activity console.</summary>
    public Action<GitCommandCompleted>? Recorder { get; init; }

    /// <summary>
    ///  Clones <paramref name="url"/> into a new folder under <paramref name="parentDirectory"/>.
    /// </summary>
    /// <param name="folderName">
    ///  The folder to create. Empty means let git derive it from the URL, matching what the command
    ///  line does.
    /// </param>
    /// <returns>The result, and the working directory to open when it succeeded.</returns>
    public (GitOperationResult Result, string WorkingDirectory) Clone(
        string url,
        string parentDirectory,
        string folderName,
        bool recurseSubmodules)
    {
        string target = string.IsNullOrWhiteSpace(folderName) ? DeriveFolderName(url) : folderName.Trim();

        if (string.IsNullOrEmpty(target))
        {
            return (new GitOperationResult("Clone", false, "Could not work out a folder name from that URL."), "");
        }

        string workingDirectory = Path.Combine(parentDirectory, target);

        if (Directory.Exists(workingDirectory) && Directory.EnumerateFileSystemEntries(workingDirectory).Any())
        {
            return (new GitOperationResult("Clone", false, $"{workingDirectory} already exists and is not empty."), "");
        }

        GitArgs arguments = new GitArgs("clone")
            .Add("--progress")
            .AddIf(recurseSubmodules, "--recurse-submodules")
            .Add(url, target);

        GitOperationResult result = Run(parentDirectory, "Clone", arguments);
        return (result, result.Succeeded ? workingDirectory : "");
    }

    /// <summary>
    ///  <see cref="Clone(string, string, string, bool)"/>, reporting progress, and stoppable. A clone that is cancelled leaves a
    ///  half-written folder behind; it is removed when the clone created it, never when it was already
    ///  there.
    /// </summary>
    public (GitOperationResult Result, string WorkingDirectory) Clone(
        string url,
        string parentDirectory,
        string folderName,
        bool recurseSubmodules,
        Action<GitProgress> onProgress,
        CancellationToken cancellationToken)
    {
        string target = string.IsNullOrWhiteSpace(folderName) ? DeriveFolderName(url) : folderName.Trim();

        if (string.IsNullOrEmpty(target))
        {
            return (new GitOperationResult("Clone", false, "Could not work out a folder name from that URL."), "");
        }

        string workingDirectory = Path.Combine(parentDirectory, target);
        bool existed = Directory.Exists(workingDirectory);

        if (existed && Directory.EnumerateFileSystemEntries(workingDirectory).Any())
        {
            return (new GitOperationResult("Clone", false, $"{workingDirectory} already exists and is not empty."), "");
        }

        GitCli git = new(parentDirectory);

        if (Recorder is Action<GitCommandCompleted> record)
        {
            git.CommandCompleted += (_, command) => record(command);
        }

        try
        {
            GitResult result = git.WriteWithProgress(
                new GitArgs("clone").Add("--progress").AddIf(recurseSubmodules, "--recurse-submodules").Add(url, target),
                onProgress,
                cancellationToken);

            return (new GitOperationResult("Clone", result.Succeeded, result.AllOutput.Trim()), result.Succeeded ? workingDirectory : "");
        }
        catch (OperationCanceledException)
        {
            if (!existed)
            {
                DeleteTree(workingDirectory);
            }

            throw;
        }
    }

    /// <summary>Removes a folder git was writing, including the read-only files git makes of its objects.</summary>
    private static void DeleteTree(string directory)
    {
        try
        {
            if (!Directory.Exists(directory))
            {
                return;
            }

            foreach (string file in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories))
            {
                File.SetAttributes(file, FileAttributes.Normal);
            }

            Directory.Delete(directory, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A file still held open: the folder stays, and saying so is the caller's job.
        }
    }

    /// <summary>Initialises an empty repository, creating the folder if it is not there yet.</summary>
    public (GitOperationResult Result, string WorkingDirectory) Init(string directory, bool bare)
    {
        try
        {
            Directory.CreateDirectory(directory);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return (new GitOperationResult("Initialise", false, ex.Message), "");
        }

        GitOperationResult result = Run(directory, "Initialise", new GitArgs("init").AddIf(bare, "--bare"));

        return (result, result.Succeeded && !bare ? directory : "");
    }

    /// <summary>
    ///  Creates <see cref="RepositoryTemplate.WorkingDirectory"/>, writes the template's files and makes
    ///  the first commit, then sets up the workflow's branches.
    /// </summary>
    /// <remarks>
    ///  There is always a first commit, empty if nothing was chosen: a branch cannot be made from an
    ///  unborn one, and Gitflow's <c>develop</c> needs something to point at.
    /// </remarks>
    public (GitOperationResult Result, string WorkingDirectory) Create(RepositoryTemplate template)
    {
        const string Description = "New repository";
        string directory = template.WorkingDirectory;

        try
        {
            if (Directory.Exists(directory) && Directory.EnumerateFileSystemEntries(directory).Any())
            {
                return (new GitOperationResult(Description, false, $"{directory} already exists and is not empty."), "");
            }

            Directory.CreateDirectory(directory);

            foreach ((string path, string text) in template.Files())
            {
                string full = Path.Combine(directory, path.Replace('/', Path.DirectorySeparatorChar));
                Directory.CreateDirectory(Path.GetDirectoryName(full) ?? directory);
                File.WriteAllText(full, text, new System.Text.UTF8Encoding(false));
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return (new GitOperationResult(Description, false, ex.Message), "");
        }

        List<GitArgs> steps =
        [
            new GitArgs("init").Add("--initial-branch", template.DefaultBranch),
            new GitArgs("add").Add("--all"),
            new GitArgs("commit").Add("--quiet", "--allow-empty", "-m", "Initial commit"),
        ];

        if (template.Workflow == RepositoryWorkflow.GitFlow)
        {
            // The keys git-flow's own init writes, so `git flow` works here without asking again.
            steps.Add(new GitArgs("config").Add("gitflow.branch.master", template.DefaultBranch));
            steps.Add(new GitArgs("config").Add("gitflow.branch.develop", "develop"));
            steps.Add(new GitArgs("config").Add("gitflow.prefix.feature", "feature/"));
            steps.Add(new GitArgs("config").Add("gitflow.prefix.bugfix", "bugfix/"));
            steps.Add(new GitArgs("config").Add("gitflow.prefix.release", "release/"));
            steps.Add(new GitArgs("config").Add("gitflow.prefix.hotfix", "hotfix/"));
            steps.Add(new GitArgs("config").Add("gitflow.prefix.support", "support/"));
            steps.Add(new GitArgs("config").Add("gitflow.prefix.versiontag", ""));
            steps.Add(new GitArgs("switch").Add("--quiet", "-c", "develop"));
        }

        List<string> output = [];

        foreach (GitArgs step in steps)
        {
            GitOperationResult result = Run(directory, Description, step);

            if (result.Output.Length > 0)
            {
                output.Add(result.Output);
            }

            if (!result.Succeeded)
            {
                // The folder stays: it is a real repository by now, and whatever failed (a missing
                // user.name, usually) is easier to fix in it than to start again.
                return (new GitOperationResult(Description, false, string.Join(Environment.NewLine, output)), directory);
            }
        }

        return (new GitOperationResult(Description, true, string.Join(Environment.NewLine, output)), directory);
    }

    /// <summary>
    ///  "https://host/owner/repo.git" and "git@host:owner/repo.git" both yield "repo" — the same name
    ///  <c>git clone</c> would pick.
    /// </summary>
    private static string DeriveFolderName(string url)
    {
        string trimmed = url.Trim().TrimEnd('/');

        if (trimmed.EndsWith(".git", StringComparison.OrdinalIgnoreCase))
        {
            trimmed = trimmed[..^4];
        }

        int separator = trimmed.LastIndexOfAny(['/', ':', '\\']);
        return separator >= 0 ? trimmed[(separator + 1)..] : trimmed;
    }

    /// <summary>
    ///  Runs git in <paramref name="workingDirectory"/>, which for these commands is a plain folder
    ///  rather than a repository.
    /// </summary>
    private GitOperationResult Run(string workingDirectory, string description, GitArgs arguments)
    {
        GitCli git = new(workingDirectory);
        if (Recorder is Action<GitCommandCompleted> record)
        {
            git.CommandCompleted += (_, command) => record(command);
        }

        // A bad URL or an unreachable host is a non-zero exit: information for the user, not an error.
        GitResult result = git.Write(arguments);
        return new GitOperationResult(description, result.Succeeded, result.AllOutput.Trim());
    }
}
