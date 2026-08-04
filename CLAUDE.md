# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Project overview

dotnet-isolate is a CLI tool that isolates the exact set of projects and dependencies required to
build a given .NET project out of a larger solution. Given a `.csproj`, it produces a folder
containing only that project and its transitive dependencies (project references + the files those
projects need to compile/run, e.g. `appsettings.json`). The intended use case is shrinking Docker
build contexts and improving layer-cache reuse in CI/CD: copy the isolated folder instead of the
whole solution, so unrelated changes elsewhere in the repo don't bust the build cache.

Usage (target CLI surface, see README.md for the full example):

    dotnet isolate path/to/<Project>.csproj [-o/--output-dir <name>]

**Status: early scaffold.** `Program.fs` in both projects is currently just a placeholder
(`printfn "Hello from F#"` / `main _ = 0`) and `Tests.fs` has a single trivial passing test. The
actual isolation logic (dependency graph construction, file discovery, hardlink/copy) has not been
implemented yet. `DESIGN.md` and `REQUIREMENTS.md` exist but are currently empty — check them first,
since they are meant to hold the authoritative design and numbered requirements as the project
matures; if they've been filled in since this doc was last updated, prefer their content over the
notes below.

## Requirements/plan (from README.md, not yet split into REQUIREMENTS.md)

- Hardlink (on Linux/Linux-containers; copy on Windows) all required folders and files into the
  isolated output folder.
- Copy/hardlink all files referenced by the csproj files (e.g. `appsettings.json` and anything else
  that gets compiled/copied in) — not just project references.
- Don't copy/hardlink anything that isn't needed by the target project.
- Analysis must be fast — near-instantaneous (<1s) even for a complex solution.
- Deterministic: running the tool twice on the same input must produce the same output, to keep the
  Docker layer cache valid.
- Must run on Windows, Linux, and macOS, targeting both .NET 8 and .NET 10.
- Planned integration test fixture: a test solution with five projects — `ServiceA`, `ServiceB`,
  `LogicA`, `LogicB`, `LogicCommon` — where `ServiceA` depends on `LogicA` + `LogicCommon` and
  `ServiceB` is analogous. This fixture does not exist yet.
- Planned design approach: build a dependency graph from the solution/csproj files, resolve the set
  of files needed, then copy/hardlink them into the output subfolder.

## Repository layout

- `src/DotnetIsolate/` — the .NET solution root (`DotnetIsolate.slnx`, an XML-based `.slnx` solution
  file rather than the legacy `.sln` format).
  - `DotnetIsolate/` — the CLI tool project (F#, `net8.0`, `OutputType=Exe`).
  - `DotnetIsolate.Test/` — xUnit test project (F#, `net8.0`).
  - `global.json` — pins the SDK to `8.0.0` with `rollForward: latestMinor`.
- `README.md`, `DESIGN.md`, `REQUIREMENTS.md` — linked into the solution under a `/Docs/` solution
  folder (see `DotnetIsolate.slnx`) so they're visible in IDEs alongside the code.

## Commands

Run all commands from `src/DotnetIsolate/` (where the solution file lives).

    dotnet build                              # build the whole solution
    dotnet test                               # run all tests
    dotnet test --filter "FullyQualifiedName~Tests.My test"   # run a single test
    dotnet run --project DotnetIsolate        # run the CLI

There is no CI pipeline configured yet; the README notes a GitHub Actions matrix (platform x
.NET version) is planned but not yet present in the repo.

## Conventions

- Source is F#, not C# — new code should follow F# idioms (modules, pipelines, immutable data)
  rather than porting C#-style OOP patterns.
- The README's stated goals include: clean code with small, easily readable functions and sensible
  namespace/module splitting; semantic versioning where every commit that passes CI and has
  sufficient coverage is released as an alpha, and tags promote non-alpha releases; eventual
  publishing to nuget.org; signed commits.
