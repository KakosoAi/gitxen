# Code signing policy

Free code signing provided by [SignPath.io](https://about.signpath.io), certificate by
[SignPath Foundation](https://signpath.org).

> Signing is being set up: releases up to and including 0.11.0 are not signed. This page is the
> policy every signed release will follow.

## What is signed

Only the Windows installer (`Gitxen-<version>-x64.msi`) published on
[GitHub Releases](https://github.com/KakosoAi/gitxen/releases), and only when it is built by this
repository's release workflow (`.github/workflows/gitxen-release.yml`) from a version tag on `main`.
Nothing built on a developer's machine is signed.

The installer contains only code from this repository and the third-party components listed in
[NOTICE.md](NOTICE.md), which are built from their official packages.

## Roles

| Role | Who |
| --- | --- |
| Committers and reviewers | Members of the [KakosoAi](https://github.com/KakosoAi) organisation with write access to this repository |
| Approvers | The KakosoAi organisation owners |

Every change reaches `main` through the repository's branch protection, and contributors outside the
organisation sign the [CLA](CLA.md). A signing request is approved by an approver in SignPath for
every release. All team members use multi-factor authentication on GitHub and SignPath.

## Privacy

Gitxen does not send any information to other networked systems unless the user asks it to: running
git commands against the user's own remotes, reading pull requests, issues and Actions through the
GitHub CLI the user has signed in to, and, only if the user adds their own key, writing commit
messages with the Anthropic API. It has no telemetry, analytics or crash reporting. See
[SECURITY.md](SECURITY.md) to report a problem.

## For maintainers: turning signing on

1. Apply at <https://signpath.org/apply> as the Gitxen project, linking this repository and this page.
2. Once accepted, in SignPath create the project `gitxen` with:
   - an artifact configuration `msi` that signs a single `.msi` file
     (`<msi-file><authenticode-sign /></msi-file>`);
   - a signing policy `release-signing` using the SignPath Foundation certificate, requiring approval;
   - a trusted build system for GitHub.com, linked to this repository.
3. In this repository, set the variable `SIGNPATH_ORGANIZATION_ID` and the secret
   `SIGNPATH_API_TOKEN` (a CI user's token). The release workflow signs from the next tag on.
