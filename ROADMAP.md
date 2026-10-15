# Roadmap

Gitxen is in beta. This is the direction, not a promise; priorities follow what people use and ask for.
Discuss or vote in [issues](https://github.com/KakosoAi/gitxen/issues) and
[discussions](https://github.com/KakosoAi/gitxen/discussions).

## Toward 1.0

- **Trust:** signed installers (SignPath), build provenance on every release, reproducible build notes.
- **Quality:** more parser and view-model tests, a smoke test that launches the app in CI.
- **Distribution:** winget, Scoop and Chocolatey listings.
- **Polish:** accessibility pass (keyboard and screen reader), localisation support, a setup wizard
  redesign.
- **Docs:** screenshots, a user guide, `gx` reference (started in [docs/](docs/)).

## Later

- Performance work on very large repositories (cached Overview reads).
- Gerrit / GitLab / Bitbucket awareness alongside GitHub.
- A plugin or theming surface, if there is demand.
- Other platforms: WinUI 3 is Windows-only, so this would be a separate front-end on
  `Gitxen.Git` and `Gitxen.Core`, not a port.

## Not planned

- Re-implementing git. Gitxen runs the git you have installed, by design.
- Telemetry. There is none, and there will not be any without an explicit opt-in.
