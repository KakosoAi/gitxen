# Changelog

All notable changes to Gitxen are listed here. The format follows
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and versions follow
[Semantic Versioning](https://semver.org). Versions below 1.0.0 are betas: anything may change.

## [Unreleased]

## [0.11.0] - 2026-10-15

### Added

- **A real terminal in the bottom pane:** each shell is drawn by xterm.js and typed into directly,
  so prompts such as Oh My Posh, colours and full-screen programs work. Font, size, cursor and colour
  scheme are taken from Windows Terminal's settings, and a button opens the folder in Windows
  Terminal itself. Ctrl+C copies a selection or interrupts; Ctrl+V and right-click paste.
- **Searchable Settings:** a search box finds any setting by the words on its card and scrolls to it.
- Tests for the GitHub parsers (against captured `gh` output) and for `GitDirectory`.
- Scoop bucket: `scoop bucket add kakoso https://github.com/KakosoAi/scoop-bucket`.

### Changed

- History is now **Timeline**, second in the sidebar after Overview.
- Git config and Maintenance moved into Settings, as categories beside General.
- The terminal's separate command box is gone.

## [0.10.1] - 2026-10-15

### Fixed

- Builds on a clean machine: the Windows SDK targeting pack is now downloaded explicitly, which
  fixes CI and the release build on GitHub's runners (NETSDK1127).
- `gx new` and `gx account auto` no longer block on git inside async commands.
- `SHA256SUMS.txt` on a release lists only the files attached to it.

### Changed

- Dependencies: VisualStudio.Threading 18.12, BannedApiAnalyzers 5.6, Microsoft.NET.Test.Sdk 18.11,
  AwesomeAssertions 9.6, SourceLink 10.0.402, and current GitHub Actions.
- The .NET SDK is pinned to 10.0.204.

## [0.10.0] - 2026-10-15

First public release, and the first under the GPL-3.0-or-later. Earlier versions were private and are
not part of this repository.

### Added

- **History page redesign:** two-line rows, author chips and a richer details card with a
  change-by-kind summary bar.
- **Search across repositories:** a local SQLite (FTS5) index of every repository's commits, found
  from Ctrl+K and from `gx search`.
- **Visual interactive rebase:** reorder, reword, squash, fix up and drop commits.
- **Line history and content search:** `log -L` for a line range, and `-G` search of changed lines.
- **AI writing (opt-in):** commit messages and pull request text through the Anthropic API; the key
  is kept in Windows Credential Manager.
- **Safety guards:** secret and large-file warnings before committing or pushing; branch cleanup;
  undo for the last operation.
- **Overview start page:** per-repository figures (languages, contributors, activity).
- **`gx` command line:** credential helper, per-repository GitHub accounts, `new`, `stats`,
  `search`, `speedup`, `open`, `templates`.
- Pull requests, issues and Actions through the GitHub CLI, with a GitHub account per repository.
- Parser unit tests (`src/app/Gitxen.Tests`) and continuous integration.

### Changed

- Licence: GPL-3.0-or-later. The project was previously proprietary.

[Unreleased]: https://github.com/KakosoAi/gitxen/compare/gitxen-v0.11.0...HEAD
[0.11.0]: https://github.com/KakosoAi/gitxen/compare/gitxen-v0.10.1...gitxen-v0.11.0
[0.10.1]: https://github.com/KakosoAi/gitxen/compare/gitxen-v0.10.0...gitxen-v0.10.1
[0.10.0]: https://github.com/KakosoAi/gitxen/releases/tag/gitxen-v0.10.0
