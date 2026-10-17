# Gitxen

Gitxen is a git client for Windows, built with WinUI 3.

It is free software under the GPL-3.0-or-later, © 2026 Kakoso — see [LICENSE](../../../LICENSE) and
[NOTICE.md](../../../NOTICE.md).

Git is run as a subprocess by `src/app/Gitxen.Git/`, Gitxen's own layer, which references nothing. The GitHub
CLI is run the same way by `src/app/Gitxen.GitHub/`, which also references nothing.
Gitxen began as a fork of [Git Extensions](https://github.com/gitextensions/gitextensions) and ran on
that project's engine until version 0.2.0; the engine, the WinForms application and the scaffolding
that came with them have since been removed, and no Git Extensions code remains.

## Building and running

```
dotnet build src/app/Gitxen/Gitxen.csproj -c Debug -p:Platform=x64
```

x64 only. Needs a real Windows SDK install, because the native
WinUI toolchain (`cswinrt.exe`, PRI tasks) reads the `KitsRoot10` registry value.

`Gitxen.slnx` at the repo root is the whole thing — this app, `Gitxen.Git` and `Gitxen.GitHub` — and is
what to open in Visual Studio:

```
dotnet build Gitxen.slnx
```

There are no tests: the inherited suites were removed with the code they covered. `HunkSplitter`,
`TerminalOutputBuffer` and `GitDirectory` are the first things worth covering when they return.

The output is `artifacts/Debug/bin/Gitxen/x64/net10.0-windows10.0.19041.0/Gitxen.exe`.

**Building is not proof it runs.** The csproj carries several workarounds for genuine .NET 10 SDK /
Windows App SDK version-resolution defects, each commented in place. Some of those failures only
appear at startup, as `FileNotFoundException` on `WinRT.Runtime` or `Microsoft.Windows.SDK.NET`.
Always launch the app after touching the csproj.

## The first run

`Views/SetupWizardView` is an overlay across the whole window, shown once when the session says setup
has not been through. Seven steps: welcome (which also reports whether git is installed at all),
identity, appearance, behaviour, a folder scan, projects, and a summary. Skipping counts as having
been through it — being asked again on every launch after saying no once is worse than never asking.

The steps are panels switched by visibility, not pages in a `Frame`, for the same reason the
repository sections are: going back a step must not discard what was typed into the step you are
returning to.

**Where each answer goes.** Identity (`user.name`, `user.email`, `init.defaultBranch`) is written to
git's global configuration through `Services/GlobalGitSettings`, which exists because
`RepositoryLoader` is built around a repository that already exists and the wizard runs before
anything has been opened. Theme, auto-fetch and the commit page size are written to `AppOptions`,
which is read on demand. Layout and UI mode are *not*: they come back on the outcome and the shell
sets them through `MainViewModel`, whose property setters are what raise the notifications the tab
strip and the repository column are bound to.

**The scan** is `Services/RepositoryScanner`: an iterative walk over an explicit stack, bounded by a
depth (4 by default), skipping dependency and build folders, and not descending into a repository
once it finds one — a repository's submodules are part of it, not separate things to import. The
branch shown against each hit is read straight out of `.git/HEAD` rather than by running git, because
a projects folder routinely holds dozens of repositories and a process per repository costs more than
the entire walk.

**Projects** are proposed from the folders the repositories were found in, which is already how people
think about them, and the proposed names are editable before anything is created. The alternative
offered is one project for everything, or none.

Imported repositories are added to Home and **not opened**: thirty tabs appearing at once would be a
worse first impression than the empty shell the wizard exists to fix. This is why `MaxRecent` in
`MainViewModel` is 60 rather than the 10 it was when that list was purely most-recently-used.

Home's **Find repositories** button reopens the wizard, which is how a second projects folder gets
imported later. Settings would be the conventional home for that, but Settings is a repository page
and is unreachable with nothing open.

A session written before the wizard existed has no `HasCompletedSetup` flag and would deserialise to
`false`, showing the first-run wizard to someone who has used the app for months. `SessionStore`
treats a session that already names repositories, groups or recent paths as one that is demonstrably
past its first run.

## Packaging

Everything that turns a build into something installable lives in
[`setup/gitxen/`](../../../setup/gitxen/README.md): the MSI, the portable archive, an MSIX, the
`irm | iex` script, and the winget / Chocolatey / Scoop manifests. One command builds the lot:

```
pwsh ./setup/gitxen/build/Build-Gitxen.ps1
```

The shipped payload is a **self-contained** publish (`-r win-x64 --self-contained
-p:WindowsAppSDKSelfContained=true`), so neither the .NET runtime nor the Windows App SDK has to be
installed on the target machine. Two properties in the csproj exist purely for that path, and both
were arrived at through a startup crash rather than a build error:

- **`RuntimeFrameworkVersion` is cleared.** `eng/RepoLayout.props` pins it to `10.0.0` for
  `Microsoft.NETCore.App`, and MSBuild applies a single value to *every* framework reference, so a
  RID-specific restore goes looking for `Microsoft.Windows.SDK.NET.Ref 10.0.0`, which has never
  existed.
- **`PublishXamlCompilerOutputs` carries the XAML compiler's output into the publish folder.** The
  `.xbf` files and the app's `.pri` are build outputs that the SDK's publish pipeline does not track,
  so `dotnet publish` drops them silently. Without the `.pri`, Microsoft.UI.Xaml cannot resolve the
  app's own XAML and the process dies before the window appears, with a stowed exception
  (`0xc000027b`) and no managed stack.

The same warning as above applies with more force here: publishing successfully is not proof the
published application starts. Run the executable out of `artifacts/Release/publish/Gitxen` after any
change to the csproj or to the packaging.

## Shape of the code

```
MainWindow            shell: custom title bar, repo tab strip, empty state
└── RepositoryView    one repository: nav pane, command bar, InfoBar for results
    └── Views/*View   one page per section, all deriving from RepositoryPage
        └── DiffPanel shared diff reader (unified / side-by-side)
```

| Layer | What lives there |
| --- | --- |
| `Views/` | One control per navigation section. `RepositoryPage` is the base: it carries the `Tab` dependency property and the dialog helpers. |
| `ViewModels/` | `MainViewModel` (open tabs), `RepositoryTabViewModel` (one repository), `WorkingDirectoryViewModel` (staging), `DiffViewModel` (one diff panel), and the GitHub set: `GitHubRepositoryViewModel` (one per tab: the gate, the client, the repository), `GitHubPullRequestsViewModel`, `GitHubIssuesViewModel`, `GitHubActionsViewModel`, with their rows in `GitHubRows.cs`. |
| `Services/` | `RepositoryLoader` — every git command for an existing repository, split by concern: history, diffs and blame (`.cs`); everything that changes the repository (`.Operations.cs`); the structured listings (`.Objects.cs`); config, state and conflicts. It is a thin layer over `Gitxen.Git`. `RepositoryCreator` — clone and init, which run in a parent directory. `GitHubService` — whether gh is installed and signed in (cached, shared by every tab) and the factory for its command runner. |
| `Models/` | Records the pages bind to: `BranchInfo`, `RemoteInfo`, `TagInfo`, `StashInfo`, `SubmoduleInfo`, `WorktreeInfo`, `ReflogEntry`, plus `RevisionQuery` and the operation option records. |
| `Diff/` | `DiffParser` (raw diff to rows), `HunkSplitter` (raw diff to applicable patches), `SyntaxHighlighter`. |
| `Terminal/` | The bottom terminal's engine: `ConPtySession` (a shell on a Windows pseudoconsole), `TerminalOutputBuffer` (VT stream back to plain lines), `TerminalSession` (one running shell), `TerminalShells` (which shells are installed). |
| `Assets/` | The Gitxen mark. `gitxen.svg` is the vector master; the PNGs beside it are rendered from the same geometry, and `gitxen.ico` bundles them for the shell. |

The tab strip is a `TabView` with no content: the repository view is hosted in its own grid row
instead, because `TabView` arranges its content at the content's desired size rather than filling the
space, which left short pages floating in a fraction of the window.

The strip lists only Home and the **open** repositories. It briefly listed every known repository,
which broke closing: the closed repository's row stayed in the strip, `TabView` coerced its selection
onto that row after the rebuild, and the selection handler read that as a click and reopened it. The
repository column still lists everything (open rows carry a close cross; remove-from-list is on the
row's context menu), and it collapses to a slim rail via the chevron in its header — the collapsed
state persists in the session.

F12 (or the console button at the right end of the status bar) toggles the git activity pane
(`Views/GitActivityPane`), a live view of `Services/GitActivity` — every process the app
launches, with the selected entry's arguments, working directory, duration and exit code. Standard
output is deliberately not kept; it is shown where it is consumed.

Sections are switched by visibility rather than navigated to in a `Frame`. A `Frame` recreates its
page on every visit, which would discard the commit list's scroll position and any half-typed commit
message.

## The mark

An X drawn as two crossing strokes on a rounded gradient tile, with commit nodes at the four
ends and a punched-out node where they meet: an X for Gitxen, and two branches meeting at a
merge point.

The nodes are drawn only at 48px and above. Below that they close up against the strokes and the
X stops reading as an X, which is the one thing it has to survive at 16px in a taskbar.

| File | Used for |
| --- | --- |
| `gitxen.svg` | Vector master. Everything else is rendered to match it. |
| `gitxen.ico` | 16-256px. `ApplicationIcon`, so the shell shows it on the executable, and `AppWindow.SetIcon`, so the taskbar and Alt+Tab show it on the window. |
| `gitxen-32.png` | The title bar. |
| `favicon.ico`, `favicon-16.png`, `favicon-32.png`, `apple-touch-icon.png`, `gitxen-logo.png` | For a site or a readme; not used by the app. |

`ApplicationIcon` alone is not enough — it only embeds the icon in the executable. An unpackaged
WinUI window carries no icon of its own until it is given one, so `MainWindow` also calls
`AppWindow.SetIcon` at startup.

## Conventions worth knowing

**Every git command goes through `RepositoryLoader`,** built as a `GitArgs` and run by `GitCli`. A
non-zero exit is information for the user (nothing to fetch, a rejected push, a dirty worktree), not
an exceptional condition: `GitCli` returns it in a `GitResult` rather than throwing. Operations return
a `GitOperationResult` and are surfaced in the repository's `InfoBar` via `Tab.Report`, not in a modal
dialog.

**Never quote an argument by hand.** Arguments reach the process one at a time through
`ProcessStartInfo.ArgumentList`, so a value with spaces, quotes or backslashes arrives intact and
quotes added by hand become part of the value. For the same reason a literal tab in a `--format`
works, though the `%09` / `%x09` escapes are still used because they keep a format one readable token.

**`ReadRecords` pads short lines rather than skipping them.** Trailing fields are routinely empty — a
branch with no upstream, a ref that is not symbolic — and the separators that would mark them are
trimmed off before parsing. Requiring a full field count silently drops exactly those rows.

**A star column needs a container that stretches.** `ListViewItem` and `ListViewHeaderItem` both
align their content to the left by default, and a left-aligned container measures its child with
unbounded width — so a star column in the template collapses and the column after it is arranged
past the visible edge. Set `HorizontalContentAlignment="Stretch"` on the item container style, and on
`GroupStyle.HeaderContainerStyle` for a grouped list. This is why several templates here once used
guessed fixed widths; they do not need to.

**Reads are capped.** 2,000 commits per page (`AppOptions.MaxCommits`) and 5,000 diff lines
(`MaxDiffLines`). This repository alone has ~17k commits, more than an unvirtualised bound collection
should hold. Paging uses `--skip` (see `GitLog`), alongside
`--max-count` set to one row **past** the page: git then stops on its own instead of being cancelled
after the fact, and the surplus row is what tells the page there is more to load. Without it a page
cost the whole 17k-commit walk (365ms against 119ms here) and parsed fifteen thousand revisions that
were thrown away.

**A page of history is laid out eagerly and drawn lazily.** `CommitGraphBuilder.AddCommit` has to run
for every commit that loads, because a lane only means anything relative to the commits already
placed — but it returns a `GraphRow` of plain coordinates, and the XAML geometry is built by
`GraphRow.Segments` when the list realises the row. Building it up front cost roughly 37,000
`PathFigure`/`LineSegment`/`Geometry` objects per page on this repository, all of them WinRT objects
constructed on the UI thread, of which the virtualised list ever showed about 500. For the same
reason `_loadedCommitCount` is a counter rather than a `Count(...)` over the loaded rows: recounting
per commit made loading a page quadratic.

Note that `MaxLanes` clamps where a lane is *drawn*, not how many lanes exist, so every lane past it
shares the overflow column's x — the builder skips those pass-through lines rather than stacking
identical ones in different colours.

**The reads a reload needs are started together, not awaited one at a time.** Each is its own git
process, and on Windows that is most of the cost (60-155ms each here), so sequencing them spent the
better part of a second before `git log` began. The three that consult the index —
`GetCurrentOperation`, `GetConflicts`, `GetWorkingDirectoryChanges` — are chained to each other
inside one task: each runs `git status` or `git diff` without `--no-optional-locks`, so they refresh
the index and can take `index.lock` to write it back, and two at once can leave one failing on the
lock. That chain is now the floor for a reload, so `GetCurrentOperation` takes its conflict count as
an argument instead of running a second `git diff` to recount what `GetConflicts` has just listed.
Everything is applied on the UI thread, in the order it was applied when the reads were sequential.

## What is read again, and what is not

The app talks to git by launching it, and on Windows a git process costs 60-90ms almost regardless of
what it is asked — so the question that decides how fast a screen feels is usually *how many
processes*, not *which commands*.

**Facts that are a file are read as a file.** `Services/GitDirectory` reads the checked-out branch
out of `.git/HEAD`. The setup wizard's scan always did this; Home did not, and paid two git processes
per repository, one after another, on startup and after every open, clone, init and wizard. At the
sixty repositories the recent list can hold that was around ten seconds of frozen Home. What genuinely
needs git — the subject of the last commit — is now read concurrently and bounded
(`MaxConcurrentRecentReads`), and Home draws last launch's text from the session
(`SessionState.RecentDetails`) while that runs. The remembered text is display-only and knowingly one
refresh stale; it is replaced as each answer arrives.

**Answers keyed by a commit are cached, because they cannot change.** A SHA names one immutable
content forever, so `RepositoryLoader` keeps a small bounded memo of the patch between two commits and
of blame at a named revision. Nothing keyed by HEAD, by a ref, or by the working tree is cached this
way: those move, and a stale answer there is a wrong one.

**Listings are reused only while a watcher is running.** Every section re-read itself on arrival, so
walking Branches → History → Branches ran `for-each-ref` twice for an answer that had not moved.
`RepositoryTabViewModel` now remembers which listings are current and clears the lot whenever
`Services/RepositoryWatcher` reports a change under `.git`. The rules that keep that honest:

- **No watcher, no reuse.** If the platform will not watch the path — a network share, a worktree
  whose `.git` is a file — `IsWatching` is false and every visit reads from git, as before. A stale
  ref listing is a wrong answer, not a slow one.
- **Invalidate coarsely.** One change empties everything. Mapping each path under `.git` to the
  listings it feeds is far more ways to be subtly wrong than re-running a few cheap commands is worth.
- **Anything outside `.git` is not reused at all**, because the watcher cannot see it: the global
  config lives in the user's home directory, and submodules are declared in the working tree's
  `.gitmodules`. Both pass `watched: false`.
- **Writes invalidate immediately, without waiting for the watcher.** `RunOperationAsync`, a config
  save and the background fetch all clear the listings themselves, because each is followed by a
  reload that would otherwise race the file-system event and redisplay what was there before.
- **Refresh means read it again.** Every section's Refresh button goes through
  `RepositoryPage.RefreshAsync`, which invalidates before activating — a Refresh that returned the
  reused answer would look like a button that does nothing.

**Session state** — open tabs, UI mode, window bounds, settings — persists to
`%LOCALAPPDATA%\Gitxen\session.json`. Restore failures are written to `restore-error.log`
beside it rather than leaving the shell silently looking like a first run. A session written by a build from before the rename is carried over from the old folder on first launch.
Settings has a "Reset application" card that deletes all of this (including the legacy file, which
would otherwise be silently migrated back) via `SessionStore.Reset()`, relaunches the executable and
closes the window; `Reset()` also disarms `Save()` so the closing window cannot write the old state
back. Repositories on disk and git configuration are untouched.

## The terminal

Ctrl+` (or the command-bar button) docks a terminal under whichever section is showing —
`Views/TerminalPane` hosted in `RepositoryView`. It offers whatever shells the machine actually has
(PowerShell 7, Windows PowerShell, cmd, Git Bash, WSL — probed by `Terminal/TerminalShells`), opens
sessions on the repository's working directory, and keeps several sessions at once. Like the panel in
an editor, the pane is shared across repository tabs; a session stays in the directory it was opened
on and says so in its title.

Each session runs its shell on a **ConPTY** (`Terminal/ConPtySession`), not on redirected stdio: a
shell behind plain pipes knows it is not interactive — PowerShell stops prompting, bash drops its
profile — while behind a pseudoconsole it behaves exactly as it does in Windows Terminal. The VT stream
it prints goes unchanged to **xterm.js** (MIT, bundled in `Assets/Terminal/`) in a WebView2, one per
session, and what is typed there goes straight back — so colours, prompts such as Oh My Posh and
full-screen programs work, and switching sessions keeps each screen as it was. The font, size, cursor
and colour scheme are read from the user's Windows Terminal settings
(`Terminal/WindowsTerminalLook`). Ctrl+C copies a selection and otherwise interrupts, Ctrl+V and
right-click paste, Ctrl+` hides the pane, and a toolbar button opens the folder in Windows Terminal
itself, which cannot be hosted inside another window. WebView2 keeps its data in
`%LOCALAPPDATA%\Gitxen\WebView2`, since the install folder is not writable.

Two things about ConPTY that are not obvious:

- **The shell exiting does not close the output pipe** — conhost holds the write end until the
  pseudoconsole is closed, so `ConPtySession` watches the process handle for exit rather than
  waiting for EOF.
- **Testing it needs a console-less parent.** Under a parent that already has a console (a test
  runner, `dotnet run`), the child attaches to that console instead of the pseudoconsole and the
  PTY sees nothing; the same code works correctly in this app because it is a GUI process. A
  windowless harness is the only honest way to exercise it outside the app.

The pane height persists like the other dragged panes (`AppOptions.TerminalPaneHeight`). Sessions are
ended explicitly when the window closes, so no shell outlives the app.

## A layout constraint you will hit

**In this app, a `Grid` column placed after a star-sized column is arranged off the right of the
screen.** The star column takes an unbounded width, so anything after it lands outside the window.

Confirmed with layout instrumentation: the element reports `Visible`, with a real size and a position
inside the window, and still does not paint. The origin was not found. Attempts that did *not* fix it:
setting `HorizontalContentAlignment` on the `UserControl`s, stretching the `TabViewItem` content,
pinning the root to the client size, and pinning the `NavigationView` content width.

Work within it:

- Toolbars are single left-aligned runs. Nothing is pinned to the right of a full-width bar.
- List row templates use fixed column widths.
- Where a star column is genuinely wanted, it goes **last** — as in the commit rows and the diff rows.

Anything under an element with an explicit width is unaffected, which is why the 400px staging pane on
the Changes page lays out normally.

## What is implemented

`gitxen <folder>`, and "Open with Gitxen" on a folder in File Explorer, open the repository that
folder is in (walking up from a subfolder), or offer to create one. A second start hands its folder
to the window already open rather than opening another: one window per installation, so a
development build never hands over to the installed copy.

The app opens on the **quick launcher**: "What are we building today?" and one search box over
every repository Gitxen knows, ranked like the go-to palette. Enter opens the choice on its Timeline
page; Esc goes to wherever the session left off; Ctrl+K brings it back over any page. Settings can
turn it off, and it is skipped when Gitxen is started with a folder.

The mode is switched from the keyboard — Ctrl+Shift+1 Simple, Ctrl+Shift+2 Advanced, Ctrl+Shift+Z
Zen, Esc to leave it — and F1 lists every shortcut. The commit graph's lines (curved, angular or rails), weight, lane spacing and colours are chosen in
Settings, with a preview, and apply to open histories at once. A repository's tab can show its branch, its
uncommitted change count and how far it is ahead of or behind its upstream, each chosen in Settings.

A first-run wizard that sets the git identity, the theme, the layout and the mode, then scans a
folder for repositories and imports the ones you pick, grouped into projects taken from the folders
they were found in. Its repository step is `Views/RepositoryImporter`, the same finder Home's
**Add repositories** opens on its own; the whole wizard can be run again from Home's Settings.

Open / clone / init, multiple repositories as tabs or a left-hand column, and a Home tab that is
always first: recent repositories with their current branch and last commit, organised into projects
you can create, colour, give an icon, collapse, and drag repositories in and out of. Group membership
is stored as paths, so a group survives its repositories being closed or temporarily unavailable.

Changes: staged and unstaged lists, stage/unstage per file and for all, **hunk-level staging**,
discard, commit, amend, sign-off. Timeline (the commit history, second in the sidebar): graph lanes, ref badges, current-branch or all-branches
scope, client-side filter plus a real `git log` search (message, author, content via `-S`, path),
commit details, per-commit context menu (cherry-pick, revert, rebase, reset, bisect, branch, tag,
checkout, copy), and — for a repository on GitHub — each commit's Actions result as a mark on its row
and a Checks list in its details, each opening the run in the browser. The commit list fills the page; clicking a changed file opens a **file viewer** over its right side
(Esc or its close button shut it, its left edge drags) with the diff, and **Blame** and **File
history** open beside it (either or both); double-clicking
a line or a commit selects that commit in the list.
Reflog with recovery actions. Conflict resolution: a side per file, a merge tool, or resolve by hand
and mark it done. Paused merges, rebases and cherry-picks are detected and shown as a banner with
their own continue/skip/abort. Branches (local and remote), Remotes, Tags, Stashes,
Submodules, Worktrees, and Settings — which also holds the repository's Git config and Maintenance,
with a search box that finds any setting by the words on its card. Fetch / pull / push with options, and continue/abort for
a paused merge, rebase, cherry-pick or revert. A dockable bottom terminal (Ctrl+`) with real shells
on ConPTY — PowerShell, cmd, Git Bash, WSL — described above.

GitHub, for repositories whose remote is on github.com: **Pull requests** (list with state, mine and
search filters, CI status filled in after the list; the selected one in full — conversation, changed
files with the same diff panel as Timeline, checks — and check out, merge, close, reopen, mark ready,
review, comment, create), **Issues** (list, detail, comment, close with a reason, reopen, create) and
**Actions** (runs with jobs and steps, failed-step and full logs, re-run all or failed, cancel, run a
workflow; a run that is still going refreshes every ten seconds while its page is showing). It is all
`gh` — see the GitHub card in Settings for the switch, the gh location and the list size — and when gh
is missing or signed out the page says so and offers the fix in place of itself. It is off for other
hosts. With several accounts signed in to gh, each repository is read as the account chosen for it
under the page title, or else the first that can see it (which is then remembered).

## What is not

A character-grid terminal (the pane is a transcript; full-screen TUI programs degrade),
interactive rebase, a file tree,
word-level diff highlighting, drag-and-drop on the commit graph, format-patch and apply-patch,
sparse checkout, submodule add/remove, GPG, LFS, the plugin host, localization (this front-end is
English-only and not wired to ResourceManager), the full settings tree, credential handling, and
crash reporting (an unhandled exception is written to `crash.log` next to `session.json`, nothing is sent). Of GitHub: reviewing line by line, releases, projects, discussions, and hosts other than
github.com.

**There are no tests.** `HunkSplitter` is the most valuable thing to cover — it is pure
string-in/string-out and it builds patches that are applied to the index. The obstacle is that it
lives in an x64 Windows App SDK assembly, so testing it needs either an x64 test project or lifting
the pure parsers into a platform-neutral one.
