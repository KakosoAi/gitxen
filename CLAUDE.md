# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## What this repository is

Gitxen: a WinUI 3 git client for Windows, **free software (GPL-3.0-or-later), © 2026 Kakoso**. It began as a
fork of [Git Extensions](https://github.com/gitextensions/gitextensions) and linked that project's
GPL-3.0 engine up to version 0.2.0. The engine, the WinForms UI, the plugins, the shell extension,
the old installer, the test suites and the inherited scaffolding have all been **removed**; the
application runs `git.exe` as a subprocess through its own layer, `src/app/Gitxen.Git/`, and reaches
GitHub through the GitHub CLI in `src/app/Gitxen.GitHub/`. See
*Licensing* below.

`src/app/Gitxen/` is the application: assembly `Gitxen.exe`, namespaces `Gitxen.*`.

**Read `src/app/Gitxen/README.md` before working in this project.** It is the real
documentation: layout, conventions, the traps, and why the non-obvious things are the way they are.

## Common commands

```
dotnet build Gitxen.slnx                  # the whole repo: the app plus Gitxen.Git and Gitxen.GitHub
dotnet build Gitxen.slnx -v:q             # quiet; do this before interpreting any error
dotnet build src/app/Gitxen/Gitxen.csproj -c Debug -p:Platform=x64
pwsh ./setup/gitxen/build/Build-Gitxen.ps1   # MSI, portable zip, package manifests
```

Requires the .NET 10 SDK pinned in `global.json` and a real Windows SDK install — the WinUI
toolchain (`cswinrt.exe`, PRI tasks) reads the `KitsRoot10` registry value. Windows-only, x64.

**Tests live in `src/app/Gitxen.Tests/`** (`dotnet test src/app/Gitxen.Tests/Gitxen.Tests.csproj
-p:Platform=x64`). They are NUnit + NSubstitute + AwesomeAssertions (never ClassicAssert, never
Moq), with names that keep the method name and add a snake_case suffix
(`MyMethod_should_return_expected`) and no Arrange/Act/Assert comments. They cover the parsers in
`Gitxen.Git`, `Diff/HunkSplitter.cs` (linked in as a source file, since the WinUI project cannot be
referenced) and `SecretScanner`. Still to cover: `Terminal/TerminalOutputBuffer.cs`,
`Services/GitDirectory.cs` and the GitHub parsers: all pure string-in/string-out.

**Building is not proof it runs.** The csproj carries several workarounds for genuine .NET 10 SDK /
Windows App SDK version-resolution defects, each commented in place. Some of those failures appear
only at startup, as `FileNotFoundException` on `WinRT.Runtime` or `Microsoft.Windows.SDK.NET`.
Launch the app after touching the csproj.

## Architecture

```
Gitxen   the application: MainWindow -> RepositoryView -> Views/*View
        |               Services/RepositoryLoader is the one seam every view goes through
        v
Gitxen.Git            GitCli (launches git), GitArgs, GitResult, and the parsers
        |
        v
git.exe               a subprocess; running it is not linking to it

Gitxen.GitHub         GhCli (launches gh), GitHubClient, GitHubParsers, GitHubModels
        |             reached through Services/GitHubService and ViewModels/GitHub*
        v
gh.exe                the GitHub CLI, a subprocess; it holds the sign-in, Gitxen stores no token
```

**Typical flow:** a view calls `RepositoryLoader` -> it builds a `GitArgs` and hands it to `GitCli`
-> `GitCli` starts `git`, one argument at a time through `ProcessStartInfo.ArgumentList` -> a parser
in `Gitxen.Git` (or the loader, for the small listings) turns the output into records -> the page
updates, and any failure is reported through the repository's `InfoBar` rather than a modal dialog.
A non-zero exit is data, not an exception: `GitCli` returns it in a `GitResult`.

**`src/app/Gitxen.Git/` references nothing** — no other project, no package. It is a plain library
that only knows how to run git and read what it says, and it must stay that way. What is in it:

| File | Does |
| --- | --- |
| `GitCli` | Launches git. `Read`/`Write` (blocking, cancellable), `ReadAsync`/`WriteAsync`, and `ReadRecords`, which streams NUL-terminated records in batches as they arrive. Reads carry `--no-optional-locks`. |
| `GitArgs` | An argument list. `Add`, `AddIf`, `AddIfNotEmpty`, `AddPaths` (emits `--`, forward slashes). |
| `GitResult` | Exit code, raw bytes, and UTF-8 readings: `Output`/`Error` (trailing newline trimmed) and `Text` (untouched). |
| `GitStatus` | Parses `status --porcelain=v2 -z` into `GitChange`. |
| `GitDiff` | Lists a commit's changed files and builds every patch read: commit, comparison, staged, unstaged, untracked. |
| `GitLog` | Streams `git log` into `GitCommit`, with filters and paging; `Follow` and `Revision` for a file's history. |
| `GitBlame` | Parses `blame --line-porcelain` into `GitBlameLine`: one entry per line with its commit, author, date and summary. |
| `GitStats` | The Overview's reads at one revision: commit dates and authors (`log -z`), the tree with sizes (`ls-tree -r -z --long`), lines per text file (`grep -I -c -z -e ""`), ref counts. |

Two rules for working in it:

- **Clean-room.** It was written from git's documented command-line behaviour and from experiments
  against `git.exe`, never by re-expressing the inherited implementation, and it must stay that way.
  Read `git help <command>` and run the command against a fixture repository; do not go looking for
  how another client parsed it. This is what makes the layer independent of the code it replaced.
- **Verify a parser against real git before committing it.** Build a fixture that contains every
  state you handle, and check the parse against git's own output on it and on a real repository, in
  both directions (nothing missing, nothing invented). Every parser here was done that way, and each
  found something that reading the documentation alone would have missed.

**Do not hand-quote arguments.** They reach the process individually, so a value with spaces,
quotes or backslashes arrives intact and any quotes added by hand become part of the value. A literal
tab in a `--format` also works, though the `%09` / `%x09` escapes keep a format one readable token.

Things about git's output that are easy to get wrong, all learned from git rather than assumed:

- **`status --porcelain=v2 -z`:** a rename's *original path is the next NUL record*, not a field,
  because `-z` leaves git no separator to use. In `XY` the two characters are the index and the
  working tree independently, so a file changed on both sides is one record that must become **two**
  entries.
- **`diff --name-status -z`:** a rename is three records — status, **old** path, new path. A rename's
  *patch* needs **both** paths in the pathspec, or git shows the file as newly added. `--no-index`
  (how an untracked file is shown) **exits 1 whenever the sides differ**, so its exit code is not
  success. Patches are read through `GitResult.Text`, not `Output`: they go back to `git apply` for
  hunk staging, and a trimmed final newline corrupts them.
- **`log`:** `-z` ends each commit with a NUL, and fields inside a commit are separated by `0x1f`
  (`%x1f`). No `--topo-order`: it makes git walk the entire history before printing anything, which
  is the page-load cost this app removed. `--max-count` and `--skip` go to git, never applied after.
  A repository with no commits has an unresolvable `HEAD`; that is an empty history, not an error.
- **`unstage`** is `reset --quiet -- <path>`, which also works before the first commit.
- **`grep -c -e ""` on a tree** counts lines in each text file without a checkout (`-I` skips
  binaries), printing `rev:path NUL count LF`. It is not `wc -l`: a blank last line is not
  counted, so a file holding only `\n` is not listed. `ls-tree --long` pads the size and prints `-`
  for a submodule.
- **`blame --line-porcelain`:** a record ends at the line that starts with a tab — the file's own
  line — so content that looks like a header is never mistaken for one. An uncommitted line is
  blamed on the all-zero id with author `Not Committed Yet`; a `boundary` key marks the oldest commit
  that could be found. A CRLF file keeps its `\r` on each line. (`blame -l`, used only to check the
  parser, drops the last hex digit of a boundary id to fit its `^`.)

**`src/app/Gitxen.GitHub/` follows the same two rules** — it references nothing, and its parsers are
checked against the real `gh` (a throwaway project that calls the client against a public repository
and against gh's own output, both directions) before they are committed. It is not clean-room in the
same sense — no client for gh was ever inherited — but it was written from `gh <command> --help` and
from experiments. Things about gh that are easy to get wrong, all learned from it:

- **Pass `-R owner/name` on every command** so gh never has to work out the repository from the
  remotes, and set `GH_PROMPT_DISABLED`, `NO_COLOR`, `GH_NO_UPDATE_NOTIFIER`, `GH_SPINNER_DISABLED`:
  a prompt on a hidden process is a hang. `GhCli` does both.
- **Two vocabularies.** Pull request states and check results come back UPPERCASE, workflow-run
  statuses lowercase, and a run's `conclusion` is an empty string until it finishes; an issue's
  `stateReason` is empty while it is open. The parsers accept either case.
- **`statusCheckRollup` is about five times slower** (3.6 s against 0.7 s for 30 pull requests) and
  mixes `CheckRun` and `StatusContext` entries, so the list is read first and the checks are a second,
  separate read that fills the dots in afterwards.
- **A workflow-level failure is a failed run with no jobs and no log** ("log not found"). That is a
  normal answer, not an error: the page explains it and links to GitHub.
- **A raw run log** is `job<TAB>step<TAB>timestamp text` per line, and the first line has a UTF-8
  BOM. `GitHubParsers.FormatRunLog` regroups it under one heading per step.
- **A run carries its commit's full id (`headSha`)**, which is how the History page marks each commit
  with its CI result from a single `run list` read instead of one read per commit.
- **`Process` writes standard input in the console code page** unless `StandardInputEncoding` is set,
  which mangled every non-ASCII comment body until `GhCli` set it to UTF-8 without a BOM.
- **`gh auth status --json hosts`** lists every signed-in account, with one active per host. Each
  repository is read as its own account: the one chosen for it (kept by `owner/name`), else gh's
  active one, else the first that can see it. A non-active account acts through `gh auth token
  --user <login>` handed back to gh as `GH_TOKEN` for that process only (`GhCli.Token`) — never
  written, logged or shown, and never `gh auth switch`, which would change every other program too.
- **Push, pull and fetch use the same account.** Git Credential Manager keeps one sign-in per host,
  so without help every github.com repository pushes as one account. `GitHubCredential.Apply`
  gives each network command `-c credential.https://github.com.helper=` (which clears the list) and
  then a shell helper that echoes `$GITXEN_GITHUB_TOKEN`. The token lives only in that process's
  environment, and the helper ignores `store`. An explicit choice also sets the repository's
  `credential.https://github.com.username`, so GCM picks the same account from a terminal.
  (`gh auth git-credential` serves only the active account, so it cannot do this.)
  Outside Gitxen, `gx credential install` puts `gx` in git's global helper list for
  `https://github.com` only, after an empty entry that clears the helpers inherited from system
  config. It saves the previous entries (often gh's, from `gh auth setup-git`) under
  `gitxen.savedGitHubHelper`, and `uninstall` restores them. The account is resolved as: the
  repository's `credential.https://github.com.username` (which git passes as `username=`), then
  Gitxen's session choice, then gh's active account. Test it with `GIT_CONFIG_GLOBAL` pointed at a
  copy, never the real global config.
- **A private repository an account cannot see is "not found"**, not "forbidden": `repo view` says
  "Could not resolve to a Repository", REST says 404, SAML enforcement 403. `GitHubClient.IsNoAccess`
  is what decides whether trying the next account is worth it.

The activity console (F12) reads `Services/GitActivity`, which every `GitCli` in the app reports to
as a command finishes. The bridge lives in the application so `Gitxen.Git` stays free of anything
UI-shaped.

| Area | Path |
| --- | --- |
| The application | `src/app/Gitxen/` |
| The layer that runs git | `src/app/Gitxen.Git/` |
| The layer that runs gh (pull requests, issues, Actions) | `src/app/Gitxen.GitHub/` |
| Shared by the window and `gx`: templates, Overview reads, speed-ups, accounts (no UI) | `src/app/Gitxen.Core/` |
| `gx`, the command line: credential helper, `new`, `stats`, `search`, `account`, `speedup`, `open` | `src/app/Gitxen.Cli/` |
| The search index: FTS5 over every repository's commits in `%LOCALAPPDATA%\Gitxen\index.db`, a rebuildable cache kept up by `SearchIndexer` | `src/app/Gitxen.Core/RepositoryIndex.cs` |
| Packaging: MSI, portable zip, winget/Choco/Scoop manifests | `setup/gitxen/` |
| Build props, rulesets, StyleCop config | `eng/` |

## Traps that have already cost time

- **A star column collapses inside a container that aligns its content left** — and whatever column
  follows it is then pushed past the visible edge. This is the cause of the "column after a star
  column disappears" problem the app worked around for a long time with fixed widths: a left-aligned
  container measures its child with unbounded width, so the star has nothing to divide. Both
  `ListViewItem` and `ListViewHeaderItem` default to left, and a grouped list needs **both**
  (`ItemContainerStyle` and `GroupStyle.HeaderContainerStyle`) set to `Stretch` — see
  `StretchedGroupHeaderStyle`. With that done, star columns work normally.
- **Sections are switched by visibility, not navigated to in a `Frame`.** A `Frame` recreates its
  page on every visit, discarding scroll position and half-typed commit messages.
- **Reads are capped and paged**, and listings are reused only while the `.git` watcher is running.
  The reasoning is in the WinUI README under *What is read again, and what is not*; do not add
  caching without reading it, because a stale ref listing is a wrong answer rather than a slow one.
- **The bottom terminal runs real shells on ConPTY.** A shell's exit is observed on its process
  handle (conhost never closes the output pipe), and a ConPTY child only attaches when the parent
  has no console, so it cannot be exercised from a console test runner.
- Session state lives in `%LOCALAPPDATA%\Gitxen\session.json`; restore failures are recorded beside
  it in `restore-error.log`.
- **Build both flavours.** A solution build writes the exe to the platform-less output folder, but
  Visual Studio runs the x64 one, which only `-p:Platform=x64` writes and which goes stale.
- **`sed -i` and heredocs in Git Bash mangle files.** `sed -i` rewrites CRLF files as LF, and a
  heredoc eats backslashes. Use the editor tools for anything containing escapes, and check line
  endings after a scripted edit.

## Hard rules

- Run git through `GitCli`, via `RepositoryLoader`; build arguments with `GitArgs`, never a raw
  concatenated string.
- Files use CRLF and must satisfy StyleCop and `.editorconfig`.
- C# 14, nullable reference types enabled. Avoid `!` suppressions.
- Never `var` for primitive types; prefer `Type t = new();` over `var t = new Type();`.
- Commit messages follow Conventional Commits.

## Licensing

Gitxen is **GPL-3.0-or-later**, © 2026 Kakoso. `LICENSE` is the verbatim GPL-3.0 text; `NOTICE.md`
lists what is included and records the project's history (it began as a fork of Git Extensions and
linked its GPL-3.0 engine up to 0.2.0; that code is gone and no earlier release lives in this
repository). The name and logo are covered by `TRADEMARK.md`, not by the licence.

- Every contribution needs the CLA in `CLA.md` (see `CONTRIBUTING.md`); that is what keeps the
  commercial option in `COMMERCIAL.md` possible. Do not accept code whose author has not agreed.
- Source files carry `SPDX-License-Identifier: GPL-3.0-or-later`; keep it on new files.
- Do not add a dependency whose licence is incompatible with the GPL-3.0 (no proprietary or
  GPL-2.0-only code). Record every shipped component in `NOTICE.md`.
- Git (GPL-2.0) and the GitHub CLI (MIT) are run as separate processes and are not bundled.

When touching anything that states the licence, keep these in step: `LICENSE`, `NOTICE.md`,
`README.md`, `src/app/Gitxen/README.md`, the About card in `Views/SettingsView.xaml`, the setup
wizard's closing line, and the manifests under `setup/gitxen/packaging/`.
