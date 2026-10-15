# Security policy

## Reporting a vulnerability

Please **do not** open a public issue for a security problem.

Report it privately with GitHub's
[private vulnerability reporting](https://github.com/KakosoAi/gitxen/security/advisories/new) on this
repository, or by email to **dev@kakoso.ai**.

Include what you found, how to reproduce it, and the version (Settings → About). You will get an
acknowledgement within a few days and a fix or a plan as soon as one exists. Please give us a
reasonable time to ship a fix before you publish details.

## Supported versions

Gitxen is in beta. Only the newest release is supported.

## What is in scope

* Gitxen itself, the `gx` command line, and the installer and package scripts in this repository.
* How Gitxen handles credentials: it stores no GitHub token (it asks `gh` for one for a single
  command), and the optional AI key is kept in Windows Credential Manager.

Problems in git, the GitHub CLI, or GitHub itself belong with those projects.
