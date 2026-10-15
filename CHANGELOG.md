# Changelog

All notable changes to Gitxen are listed here. The format follows
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and versions follow
[Semantic Versioning](https://semver.org). Versions below 1.0.0 are betas: anything may change.

## [Unreleased]

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

[Unreleased]: https://github.com/KakosoAi/gitxen/compare/gitxen-v0.10.0...HEAD
[0.10.0]: https://github.com/KakosoAi/gitxen/releases/tag/gitxen-v0.10.0
