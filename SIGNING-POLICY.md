# Code Signing Policy — Agent Usage Monitor

This document describes how release binaries of **Agent Usage Monitor**
(repository: <https://github.com/Weaverloft-AILabs/Agent-Usage-Monitor-Win>)
are built and code-signed. It is published to satisfy the transparency
requirements of the [SignPath Foundation](https://signpath.org/) free code
signing program for open-source projects.

## Project

- **Product:** Agent Usage Monitor — a Windows taskbar widget that shows local
  Claude Code CLI usage. Free and open source.
- **License:** GNU Affero General Public License v3.0 (AGPL-3.0-only). See
  [LICENSE](LICENSE).
- **Maintainer / copyright holder:** WeaverLoft inc. AILabs.
- **Source of truth:** this GitHub repository only. All released binaries are
  built from tagged commits in `master` via GitHub Actions.

## Build & release process

1. A release is triggered by pushing a `v*` tag (created by
   [`bump-version.ps1`](bump-version.ps1), which bumps `<Version>` in the app
   `.csproj`, commits, tags, and pushes).
2. GitHub Actions ([`.github/workflows/release.yml`](.github/workflows/release.yml))
   runs on a `windows-latest` runner and:
   - restores and runs the unit tests (`dotnet test`),
   - publishes a self-contained single-file executable
     (`dotnet publish -c Release -r win-x64 --self-contained`),
   - packages the installer with Velopack (`vpk pack`),
   - uploads the installer and update feed to the GitHub Release.
3. No build step runs on a maintainer's local machine for released artifacts;
   the published binaries are exactly what the CI produced from the tagged
   source.

## Signing

- Only the project maintainers (who also own this repository and its source
  code) request signatures, and only for artifacts built by the CI pipeline
  above from this repository's own source.
- Code signing is performed through SignPath. The signing request is submitted
  from the GitHub Actions workflow using the official
  [`signpath/github-action-submit-signing-request`](https://github.com/SignPath/github-action-submit-signing-request)
  action; the private key is generated and held on SignPath's HSM and is never
  exposed to the CI runner or to any maintainer.
- The certificate is issued to **SignPath Foundation**, which therefore appears
  as the publisher of the signed binaries, vouching that they were built from
  this open-source repository.

## Roles

- **Author:** the CI pipeline / maintainers who submit signing requests for
  tagged releases.
- **Reviewer / Approver:** a project maintainer reviews and approves each
  signing request before a signature is issued.
- Maintainer accounts used for signing have multi-factor authentication
  enabled.

## Integrity & reporting

- Every signed release corresponds to a public Git tag; anyone can reproduce the
  build from the tagged source and compare.
- Security concerns or suspected misuse of the signing certificate can be
  reported via the repository's issue tracker or the in-app **문의하기**
  (Inquiry) feature.
