# Versioning and Releases

## Branching: trunk-based
- `main` is always releasable. Work happens on short-lived branches (`feat/...`, `fix/...`) merged through PRs with **squash merge**.
- The PR title becomes the commit on `main` and must follow Conventional Commits (checked by the `pr-title` workflow).
- **Maintenance branches** only when a patch is needed on an older released version: `release/X.Y` created from tag `vX.Y.0`, fix, release `vX.Y.Z+1`, then cherry-pick to `main` if relevant.

## Feature flow with a spec
1. **Spec and plan** on a `docs/spec-NNN` branch: `/spec`, approval, `/plan`; PR `docs: spec and plan NNN ...` merged as soon as it is approved (minutes, not days). A `docs:` commit does not produce a release.
2. **Implementation** from an up-to-date `main`, with one short-lived branch per PR in the plan (e.g. `feat/NNN-foundations`, `feat/NNN-setup`). Never keep developing on the spec branch.
3. If implementation reveals a wrong assumption: the spec goes back to In Review and the fix goes through a `docs:` PR before the code.

## Conventional Commits
| Type | Version effect | In the changelog |
|---|---|---|
| `feat:` | minor | Features |
| `fix:` | patch | Bug Fixes |
| `perf:` | patch | Performance |
| `feat!:` / `BREAKING CHANGE:` | major (minor before 1.0) | Highlighted |
| `docs:`, `refactor:`, `test:`, `build:`, `ci:`, `chore:` | none | Hidden |

- Security fixes use `fix(security): ...`.
- Features coming from a spec reference it: `feat: first run and first response (spec 001)`.
- Commits, PRs, specs, plans, docs and code comments are written in English.

## Version: SemVer computed from git
- **MinVer** reads the latest `vX.Y.Z` tag: the version ends up in the assemblies, the Docker image and `/api/version`.
- Between tags the version is a pre-release (e.g. `0.2.0-alpha.0.5`).
- Before 1.0, breaking changes bump the minor version.

## Releasing with release-please
1. Every push to `main` updates a **release PR** with the computed version and `CHANGELOG.md`.
2. Merging that PR creates the `vX.Y.Z` tag and the GitHub Release with the notes.
3. The tag triggers the image build (pipeline to be defined).

## What "breaking" means for Quorum
A major version (minor before 1.0) is required for an incompatible change to:
- the public API;
- the declarative format of agents, tools, topologies;
- environment variables or `docker-compose.yml`;
- the license key format;
- database migrations without an automatic upgrade path.

## Rules for self-hosted customers
- Forward-only migrations: no downgrade; document backups before every upgrade.
- Docker image tags: `X.Y.Z`, `X.Y`, `X`, `latest`.
- Version visible in the UI and at `/api/version`.
- "What's new": the release notes are bundled in the image and shown to the administrator on the first login after an upgrade (future spec).
