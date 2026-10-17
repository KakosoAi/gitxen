# Getting started

## Install

Requires Windows 10 (1809) or later, x64, and [git](https://git-scm.com/download/win) installed.
Packages are self-contained: no separate .NET or Windows App SDK install is needed.

- **MSI or portable zip:** from the [releases page](https://github.com/KakosoAi/gitxen/releases).
  Gitxen is in beta, so releases are marked *Pre-release*.
- **PowerShell one-liner** (per-user, no admin, checks SHA-256):

  ```powershell
  irm https://raw.githubusercontent.com/KakosoAi/gitxen/main/setup/gitxen/install.ps1 | iex
  ```

Verify a download against `SHA256SUMS.txt` on the same release:

```powershell
Get-FileHash -Algorithm SHA256 .\Gitxen-0.11.0-x64.msi
```

## First run

The setup wizard asks for your git identity, how Gitxen should look and behave, and then lists the
repositories it finds in the usual code folders (`source\repos`, `Documents\GitHub`, `Projects`,
`dev` and the like) for you to put on Home. Everything can be changed later in Settings, which can
also run the setup again.

## Adding more repositories

Home's **Add repositories** button opens the same finder on its own, without the rest of setup:

- The usual code folders are offered as chips, and **Add a folder…** scans any other. Several
  folders can be on at once, and results appear as they are found.
- Filter the list by name or folder, and pick what to add. Repositories already on Home are listed
  but cannot be added twice.
- Put them on Home without a project, in a project per folder, in an existing project, or in a new
  one.

## Working with repositories

- Open one with **Ctrl+O**, or find any repository, branch, commit or action with **Ctrl+K**.
- Several repositories stay open at once as tabs, and are restored the next time you start.
- Staging works per file and per hunk. Commits, branches, tags, stashes, submodules, worktrees,
  reflog recovery and conflict resolution all live in the repository view.
- **F12** shows every git command Gitxen runs, with its output and timing.

## GitHub

Pull requests, issues and Actions runs appear for github.com repositories when the
[GitHub CLI](https://cli.github.com) is installed and signed in. Gitxen stores no token. With
several accounts signed in, each repository uses the account that can see it, or the one you chose.

## Where Gitxen keeps things

Settings and per-repository state are under `%LOCALAPPDATA%\Gitxen`, private to your Windows account.
The commit search index (`index.db`) is a cache and can be deleted safely.
