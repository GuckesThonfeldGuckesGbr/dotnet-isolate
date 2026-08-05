# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Project overview

dotnet-isolate is a CLI tool that isolates the exact set of projects and dependencies required to
build a given .NET project out of a larger solution. Given a `.csproj`, it produces a folder
containing only that project and its transitive dependencies (project references + the files those
projects need to compile/run, e.g. `appsettings.json`). The intended use case is shrinking Docker
build contexts and improving layer-cache reuse in CI/CD: copy the isolated folder instead of the
whole solution, so unrelated changes elsewhere in the repo don't bust the build cache.

Usage:

    dotnet isolate path/to/<Project>.csproj [-o/--output-dir <name>] [-s/--solution <path/to/.sln(x)>]

**Status: feature-complete for a v0.1 release, not yet published.** The full pipeline described in
DESIGN.md (steps 1-7: project graph resolution, per-project file resolution, solution discovery,
mirror-root computation, link-strategy probe, materialization, scoped solution-file generation) is
implemented and covered by unit, integration, and Docker E2E tests. Both `.sln` and `.slnx` source
solutions are supported. `DESIGN.md` and `REQUIREMENTS.md` are populated and authoritative — read
them first for the actual pipeline algorithm and numbered requirements (`FR-*`, `QP-*`, etc.); this
doc is a navigation aid, not the source of truth. Not yet done: the package hasn't been published to
nuget.org, and no `v0.1` tag has been pushed.

## Repository layout

- `src/DotnetIsolate/` — the .NET solution root (`DotnetIsolate.sln`, classic format — an earlier
  `.slnx` attempt hit an SDK 8 build failure and was reverted, see DESIGN.md).
  - `DotnetIsolate.Core/` — the isolation logic (project graph, file resolution, link-strategy
    detection, output materialization, `.sln`/`.slnx` filtering). No CLI/console concerns here.
  - `DotnetIsolate/` — the CLI entrypoint (F#, `net8.0`, Argu-based), a thin wrapper over Core.
  - `DotnetIsolate.UnitTests/` — fast, isolated tests of Core's pure logic (QP-5 gate: ≥95%
    coverage, IO-touching code excluded via `coverage.unit.runsettings`).
  - `DotnetIsolate.IntegrationTests/` — exercises the whole tool end-to-end, including dogfooding
    against this repo's own `.sln` and against the fixtures under `src/TestSolutions/` (QP-4 gate:
    ≥90% coverage of the whole Core assembly).
  - `DotnetIsolate.E2ETests/` — drives real `docker build` runs against both Dockerfile patterns
    from README.md's Docker integration section, asserting cache-hit behavior (QP-12). Requires a
    Docker daemon; not part of the coverage gates.
  - `DotnetIsolate.PerformanceTests/` — clones a pinned commit of the real, actively-maintained
    OrchardCMS/OrchardCore solution (241 projects) and isolates one of its projects, writing
    wall-clock time and graph size to a `customSmallerIsBetter`-shaped JSON file
    (`BenchmarkResults.fs`) with no hard pass/fail threshold (machine speed varies too much for a
    reliable gate; PR-1 is phrased in terms of graph depth, not an absolute time - see
    REQUIREMENTS.md). Not part of the coverage gates, but runs as its own `performance` job in
    build.yml on every push to `main` and every PR, charting history via
    benchmark-action/github-action-benchmark on the `gh-pages` branch (only pushed on a real commit
    to `main`; PR runs just compare against it) - informational only, never fails the build, not a
    dependency of `publish`. Needs network access (clones from GitHub) and a .NET 10 SDK
    (OrchardCore targets net10.0).
  - `global.json` — pins the SDK to `8.0.0` with `rollForward: latestMinor`.
- `src/TestSolutions/` — two real, git-tracked 5-project fixture solutions (`ServiceA`/`ServiceB`/
  `LogicA`/`LogicB`/`LogicCommon`) used by the integration and E2E suites:
  `DiamondWithIncludedFiles/` (`.slnx`, `net10.0`) and `DiamondWithIncludedFilesSln/` (`.sln`,
  `net8.0`, so it builds on every CI leg without needing a newer SDK).
- `README.md`, `DESIGN.md`, `REQUIREMENTS.md` — linked into the solution under a `/Docs/` solution
  folder so they're visible in IDEs alongside the code.
- `.github/workflows/build.yml` — CI: `test` (OS × .NET 8/10 matrix), `coverage` (QP-4/QP-5 gates),
  `e2e` (QP-12 Docker suite), `performance` (PR-1 benchmark tracking, informational only, not a
  `publish` dependency), `publish` (needs `test`/`coverage`/`e2e`; pushes a prerelease to nuget.org
  on every green push to `main`, or a stable release on a `v<major>.<minor>` tag, via NuGet Trusted
  Publishing/OIDC).

## Commands

Run all commands from `src/DotnetIsolate/` (where the solution file lives).

    dotnet build -c Release                   # build the whole solution
    dotnet test -c Release --no-build         # run all three test projects
    dotnet run --project DotnetIsolate        # run the CLI

To run a single project or filter tests, pass its path explicitly (passing multiple project paths
to a single `dotnet test` invocation fails with `MSB1008`):

    dotnet test DotnetIsolate.UnitTests/DotnetIsolate.UnitTests.fsproj -c Release --no-build
    dotnet test DotnetIsolate.UnitTests/DotnetIsolate.UnitTests.fsproj --filter "FullyQualifiedName~keeps only project entries"

The `.slnx`/`net10.0` fixture tests (in `DiamondWithIncludedFilesTests.fs`) use
`Xunit.SkippableFact` and skip gracefully if only a .NET 8 SDK is installed, rather than failing.

Coverage gates, matching what CI's `coverage` job runs:

    dotnet test DotnetIsolate.UnitTests/DotnetIsolate.UnitTests.fsproj -c Release --no-build \
      --collect:"XPlat Code Coverage" --settings DotnetIsolate.UnitTests/coverage.unit.runsettings \
      --results-directory coverage/unit
    dotnet test DotnetIsolate.IntegrationTests/DotnetIsolate.IntegrationTests.fsproj -c Release --no-build \
      --collect:"XPlat Code Coverage" --settings DotnetIsolate.IntegrationTests/coverage.integration.runsettings \
      --results-directory coverage/integration
    python3 ../../.github/scripts/check-coverage.py \
      "coverage/unit/**/coverage.cobertura.xml" 95 "coverage/integration/**/coverage.cobertura.xml" 90

## Conventions

- Source is F#, not C# — new code should follow F# idioms (modules, pipelines, immutable data)
  rather than porting C#-style OOP patterns.
- Files with IO-touching logic are split from their pure-logic counterparts (e.g.
  `FileResolution.fs` / `FileResolutionIo.fs`) specifically so `coverage.unit.runsettings` can
  exclude the IO half by class name — see DESIGN.md for why this is a per-invocation coverlet
  filter rather than `[<ExcludeFromCodeCoverage>]`.
- Integration/E2E tests genuinely shell out (`dotnet build`, `docker build`) rather than asserting
  on plausible-looking text — proving output is really buildable/cacheable, not just well-formed.
- Commits are GPG/SSH-signed (QP-8).
