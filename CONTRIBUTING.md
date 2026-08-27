# Contributing to BGPLite

## Branch model

`main` is the protected default branch:

- direct push is disabled when branch protection is enabled
- every change should go through a Pull Request
- wait for CI/checks and review before merging
- emergency hotfixes should still prefer a short-lived branch and PR

This keeps the history clear and reduces the risk of accidental pushes to the wrong branch.

## Workflow

The commands below describe the normal human contributor workflow. AI agents must follow AGENTS.md restrictions and must not execute git/GitHub mutation commands unless explicitly authorized.

Before starting work, ensure the working tree is clean or preserve existing user changes. Do not switch branches while unrelated changes are present.

```bash
# always start from fresh main
git switch main
git pull --ff-only

# branch per change
git switch -c fix/short-description     # or feature/, chore/, docs/
# ... edit, commit ...
git push -u origin fix/short-description

# open PR (via gh CLI)
gh pr create --fill
# merge after CI/checks and review pass
gh pr merge --squash --delete-branch
```

Branch prefixes are a loose convention, not a hard rule:

| Prefix | When |
|---|---|
| `feature/` | new functionality |
| `fix/` | bug fix |
| `chore/` | tooling, deps, CI, refactor without behavior change |
| `docs/` | docs only |

## Commit messages

Commits should use one standard template.

Template:

```text
<scope>: <imperative summary>
```

Rules:

- English only
- lowercase scope
- short imperative summary
- no trailing period
- keep it specific to one logical change

Preferred scopes are based on the touched area:

| Scope | When |
|---|---|
| `protocol` | `BGPLite.Protocol/` changes |
| `server` | `BGPLite.Server/` changes |
| `routing` | `BGPLite.Routing/` changes |
| `config` | `BGPLite.Configuration/` changes |
| `api` | `BGPLite.Api/` changes |
| `providers` | `BGPLite.Providers/` changes |
| `tests` | `BGPLite.Tests/` test-only changes |
| `docs` | README, docs, contributing changes |
| `workflow` | CI / Docker / GitHub Actions |
| `fix(<area>)` | focused bug fix when that reads better |

Examples:

```text
protocol: validate OPEN message hold time
server: handle graceful session shutdown on NOTIFICATION
routing: add community-based route filtering
api: persist peer custom prefixes
config: load net10.0 framework
providers: refresh RIPE Stat cache on configurable interval
fix(server): close socket after NOTIFICATION Cease
docs: update README with API examples
```

## Pull Request titles

Pull Request titles should follow the same template as commit messages.

Template:

```text
<scope>: <imperative summary>
```

Examples:

```text
protocol: validate OPEN message hold time
server: handle graceful session shutdown
fix(api): return correct peer count
```

PR body is free-form, but should usually include:

- summary
- why
- validation
- risk or possible regressions

## Local development

General build/run/test commands live in `README.md` and `AGENTS.md` ("Build and validation commands"). What is specific to the contributor workflow:

Before opening a PR:

```bash
dotnet format
dotnet test
```

Run formatting without introducing unrelated formatting changes (format only the files you touched when practical — see AGENTS.md).

Bug fixes should include a regression test when the behavior can reasonably be tested automatically.

For protocol or server changes, perform a manual interoperability smoke test with at least one real BGP implementation when practical (e.g. FRR, BIRD, GoBGP).

## Project structure

See `docs/ARCHITECTURE.md` for the current project structure and dependency boundaries.

## Configuration and state

BGPLite reads `appsettings.yml` (or path from `--config` argument) from the data directory. The data directory defaults to `./data` and can be overridden with the `BGPLITE_DATA` environment variable.

The SQLite database (`bgplite.db`) and local prefix file (`nets.txt`) live in the data directory.

Do not commit personal config files, database files, or local prefixes.

## Environment variables

| Variable | Default | Description |
|---|---|---|
| `BGPLITE_DATA` | `./data` | Data directory for SQLite DB and nets.txt |

## License

See the root `LICENSE` file for details.
