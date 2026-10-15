# Notices

Gitxen is free software, © 2026 Kakoso, licensed under the GNU General Public License,
version 3 or any later version. See [LICENSE](LICENSE).

## Programs Gitxen runs

Gitxen does its work by running **git** as a separate program, the same way a terminal does. Git is
not bundled with Gitxen, is not linked into it, and is licensed by its own authors under GPL-2.0.
Running a program is not the same as including it, so git's licence places no condition beyond
Gitxen's own. Git must be installed separately.

## Components Gitxen includes

| Component | Licence |
| --- | --- |
| Windows App SDK / WinUI 3 (Microsoft) | MIT |
| C#/WinRT (Microsoft) | MIT |
| .NET runtime and libraries (Microsoft) | MIT |
| Anthropic C# SDK (Anthropic), for the optional AI writing | MIT |
| Microsoft.Data.Sqlite and SQLite, for the local search index | MIT / public domain |
| xterm.js and its fit addon (The xterm.js authors), for the terminal pane | MIT |

Each is redistributed under its own terms.

## History

Gitxen began in 2026 as a fork of
[Git Extensions](https://github.com/gitextensions/gitextensions), and for a time its user interface
ran on that project's engine — `GitCommands`, `GitExtUtils`, `GitExtensions.Extensibility` and
`GitUIPluginInterfaces`, all GPL-3.0. While that was the case Gitxen was a derivative work and was
published under GPL-3.0; **versions up to and including 0.2.0 were released under that licence and
remain under it** for everyone who received them.

That history is why Gitxen is GPL-3.0-or-later. Those four projects have since been deleted. Their place is taken by `src/app/Gitxen.Git/`, written
from git's documented command-line behaviour and from experiments against `git.exe` — not adapted
from the code it replaced. No Git Extensions code is compiled into, linked by, or shipped with
Gitxen any more. Gitxen's own code is licensed under the GPL-3.0-or-later; the versions
published in this repository start at 0.10.0 and carry no earlier releases.

Gitxen also runs the GitHub CLI (`gh`, MIT licence) as a separate program, when the user has installed
it, to show pull requests, issues and workflow runs. It is not bundled, linked or modified. Gitxen
stores no credential: to act as one of gh's signed-in accounts other than the active one, it asks gh
for that account's token and passes it straight back to gh for that command.

With thanks to the Git Extensions authors and contributors, whose project this one started from.
