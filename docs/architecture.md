# Architecture

```
Gitxen          the WinUI 3 application: MainWindow -> RepositoryView -> Views/*View
   |            Services/RepositoryLoader is the one seam every view goes through
Gitxen.Core     shared by the window and gx: templates, creation, Overview reads, speed-ups,
   |            accounts, credential helper, secret scanner, search index (SQLite FTS5)
Gitxen.Git      GitCli (launches git), GitArgs, GitResult and the parsers
Gitxen.GitHub   GhCli (launches gh), GitHubClient, parsers, models
Gitxen.Cli      gx
Gitxen.Tests    parser and helper tests
```

## Principles

- **git is a subprocess.** `GitCli` starts `git.exe` one argument at a time through
  `ProcessStartInfo.ArgumentList`; a non-zero exit is data (`GitResult`), not an exception. Arguments are
  built with `GitArgs`, never concatenated.
- **`Gitxen.Git` and `Gitxen.GitHub` reference nothing:** no project, no package. They only know how to
  run a program and read what it prints.
- **Parsers are verified against real git.** Each is checked on a fixture repository in both
  directions (nothing missing, nothing invented) before it is committed.
- **Reads are capped and paged**, and listings are reused only while the `.git` watcher runs.
- **Failures go to the repository's `InfoBar`**, not a modal dialog.
- **Sections are switched by visibility**, not navigated in a `Frame`, so scroll position and
  half-typed commit messages survive.

For the details, including the traps that have already cost time, read
[../CLAUDE.md](../CLAUDE.md) and [../src/app/Gitxen/README.md](../src/app/Gitxen/README.md).
