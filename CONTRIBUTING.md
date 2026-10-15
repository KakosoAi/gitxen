# Contributing to Gitxen

Thanks for wanting to help. Gitxen is a beta, so the most useful things are bug reports with a way to
reproduce them, and small, focused pull requests.

## Before you start

* **Bugs and ideas:** open an issue first for anything larger than a small fix, so we can agree on the
  shape before you spend time on it.
* **Security problems:** do not open a public issue — see [SECURITY.md](SECURITY.md).
* **Conduct:** everyone taking part is bound by the [Code of Conduct](CODE_OF_CONDUCT.md).

## Licence and the CLA

Gitxen is licensed under the [GPL-3.0-or-later](LICENSE). By opening a pull request you confirm that you
agree to the [Contributor Licence Agreement](CLA.md): you keep the copyright in your work, you grant
Kakoso the right to distribute it under the GPL and under other terms (that is what keeps the
[commercial option](COMMERCIAL.md) possible), and you have the right to contribute it.

Say so in the pull request description by writing: `I agree to the Gitxen CLA.`

## Working on the code

Read [CLAUDE.md](CLAUDE.md) and [src/app/Gitxen/README.md](src/app/Gitxen/README.md) first; they hold
the layout, the conventions and the traps.

```
dotnet build Gitxen.slnx
dotnet build src/app/Gitxen/Gitxen.csproj -c Debug -p:Platform=x64
```

* Requires the .NET 10 SDK (see `global.json`) and a Windows SDK install. Windows, x64.
* Run git through `GitCli` and build arguments with `GitArgs`; never concatenate a command string.
* Files use CRLF and must satisfy StyleCop and `.editorconfig`; C# 14, nullable reference types on.
* New source files start with `// SPDX-License-Identifier: GPL-3.0-or-later`.
* `Gitxen.Git` and `Gitxen.GitHub` reference nothing; keep them that way.
* A parser change is checked against real `git` / `gh` output on a fixture, in both directions.
* Commit messages follow [Conventional Commits](https://www.conventionalcommits.org).

## Pull requests

* One change per pull request, with a description of what and why.
* Building is not proof it runs: launch the app if you touched the csproj or anything visual.
* Do not add a dependency without saying why, and its licence must be compatible with the GPL-3.0.
* Do not include code you did not write, or whose licence you cannot confirm.
