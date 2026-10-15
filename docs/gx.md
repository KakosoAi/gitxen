# `gx`, the command line

`gx` ships with Gitxen and shares its engine: the same git layer, templates, accounts and search index.
It exits 0 on success, 1 when git or gh refused (its message is printed) and 2 for a malformed command
line.

| Command | What it does |
| --- | --- |
| `gx open [path]` (or `gx .`) | Open a repository in the Gitxen window |
| `gx stats [path] [--json]` | Commits, languages, contributors and activity |
| `gx search <words> [--here] [--limit N]` | Find commits in every indexed repository |
| `gx new <name> [options]` | Start a repository from a workflow and starter files |
| `gx account` | Which GitHub account this repository uses, and the others signed in |
| `gx account use <login>` | Use that account here, for gh and for git push/pull |
| `gx account auto` | Go back to gh's active account |
| `gx credential install \| uninstall \| status` | Let git ask `gx` for each repository's GitHub account |
| `gx speedup [--status] [--watcher \| --no-watcher]` | Turn on git's speed-ups for large repositories |
| `gx templates [gitignore \| licenses]` | What `gx new` can start with |
| `gx version` | Print the version |

## `gx new` options

| Option | Meaning |
| --- | --- |
| `--in <folder>` | Where to create it (default: here) |
| `--flow <workflow>` | `github-flow` (default), `gitflow`, `trunk` or `none` |
| `--branch <name>` | Default branch (default: `main`) |
| `--gitignore <template>` | A GitHub `.gitignore` template, e.g. `VisualStudio`, `Node`, `Python` |
| `--license <key>` | A licence, e.g. `mit`, `apache-2.0` |
| `--description <text>` | One line for the README and GitHub |
| `--no-readme`, `--no-gitattributes` | Leave those files out |
| `--github private\|public` | Also create it on GitHub and push |

## Credential helper

`gx credential install` puts `gx` first in git's helper list for `https://github.com` only, saving the
previous helpers so `uninstall` can restore them. The account for a repository is resolved as: the
repository's `credential.https://github.com.username`, then the choice made in Gitxen, then gh's
active account. The token lives only in the environment of the one git process that needs it.
