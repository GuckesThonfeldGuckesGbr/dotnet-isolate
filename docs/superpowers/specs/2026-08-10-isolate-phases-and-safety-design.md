# Output-directory safety, restore phase, missing files, and multiple entry projects

Date: 2026-08-10

Five problems found while using the tool on a real solution. Four are behavioural and share the
same pipeline and CLI surface; the fifth is the README, folded in here because it documents the
flag names the other four settle.

## Problem statement

1. **The output directory can destroy the source tree.** `Materialize.materialize` deletes
   `outputRoot` recursively (FR-7) before validating anything. Reproduced: with `-o` pointing at the
   solution root, the entire source tree was deleted and the run then failed with
   `Hardlink.create failed for .../ServiceA.csproj -> (same path) (error 2)`. A second reproduction:
   with `-o` inside a project directory, the first run succeeds and the second fails, because the
   previous output is globbed back in as `Compile` items and the tool tries to link
   `out/LogicA/LogicAClass.cs` into `out/ServiceA/out/LogicA/LogicAClass.cs`.

2. **No way to split the output for Docker layer caching.** A change to any file in the isolated
   closure changes the whole output, so `COPY --from=isolate /isolated /src` invalidates and
   `RUN dotnet restore` reruns even when no package reference changed.

3. **A referenced file that does not exist on disk fails the whole run.** A `.csproj` referenced
   `<None Include="..\.dockerignore"/>`; the `.dockerignore` excluded itself from the Docker build
   context, so inside the isolate stage the file was absent. MSBuild still reports it as a `None`
   item — item resolution does not consult the disk — and materialization died copying it.

4. **Only one entry project can be isolated per run.** Some deployments need two projects
   (e.g. a `Login` service and a `Login.Qs` service) in one isolated tree.

5. **The README's two Docker patterns do not deliver the promised caching in practice**, and the
   self-contained one copies the whole context into the isolate stage with `COPY . /src`, which is
   the cost the tool exists to avoid.

## Findings that constrain the design

These were verified during design; they are recorded because they contradict the obvious approaches.

**`COPY --from` is content-keyed and mtime-insensitive.** Verified with buildx 0.36.0 / Docker
29.7.1: a stage was forced to rerun, one of its output files had its mtime moved to 2030 with
content unchanged, and a second file's content changed. The `COPY --from` of the unchanged-content
file was `CACHED`, and so was the expensive `RUN` between the two `COPY`s; only the `COPY` of the
changed file reran. This is what makes the restore phase worth building — and it matters
specifically because the tool hardlinks, so output files inherit source mtimes, and a fresh CI clone
stamps every file with a new mtime. Had mtime counted, the split would buy nothing.

**`CopyToOutputDirectory` cannot distinguish build-irrelevant files.** The original proposal for
problem 3 was to drop `None` items not copied to the output directory. Querying real MSBuild shows
the metadata is *unset* by default:

```
'app.config'       | CopyToOutputDirectory= None   (unset)
'appsettings.json' | CopyToOutputDirectory= None   (unset)
'notes.txt'        | CopyToOutputDirectory= 'Never'
'../.dockerignore' | CopyToOutputDirectory= None   (unset)
```

Such a rule would drop `app.config`, which the SDK consumes to generate `App.exe.config` and binding
redirects, and `appsettings.json` in any non-web project. The rule is rejected; the real cause of
problem 3 was file absence, not item type.

**The SDK's default `None` glob collects every otherwise-unmatched file in a project directory.**
`appsettings.json` above was never declared. This is why `packages.lock.json` already reaches the
file set without special collection, and why the restore phase must be defined by role rather than
by item type.

**`.sln` plus `.csproj` alone is not a restorable tree.** `dotnet restore` evaluates
`Directory.Build.props`, `Directory.Packages.props`, `NuGet.config` and `global.json`; under Central
Package Management a restore without `Directory.Packages.props` fails outright.

## A. Output-directory safety

**Hard error on self-consumption.** Before any filesystem mutation, fail when the output directory
equals, or is an ancestor of, any resolved source file. The error names the offending path and
explains that the output directory would consume its own inputs. This is problem 1's first
reproduction; the source tree must be intact after the failure.

**No deletion by default.** `Materialize.materialize` no longer deletes `outputRoot`. Files are
placed over whatever is present. When the output directory already contains entries this run did not
produce, the CLI warns and lists them. This supersedes FR-7's delete-and-recreate as the default.

**`--clean` flag.** Restores delete-and-recreate for callers who want the output to contain exactly
the isolated set. `--clean` is subject to the same self-consumption check, which runs first.

**Output excluded from the input set.** Every resolved path under the output directory is filtered
out before the mirror root is computed. Without this, problem 1's second reproduction recurs.

New pure module `OutputSafety.fs`: validation and filtering, no IO. It reuses `MirrorRoot.isUnder`,
which compares path segments case-insensitively and is therefore already correct on Windows.

## B. `--restore`

`--restore` emits only the files `dotnet restore` needs in order to evaluate the project graph.
Without the flag, behaviour is unchanged and the complete set is emitted. Producing both halves
means two invocations into two output directories.

*Accepted trade-off:* this evaluates MSBuild twice, doubling the pipeline's dominant cost (PR-1).
A single-invocation variant producing both halves was considered and rejected in favour of the
smaller flag surface. This is a deliberate choice, not an oversight.

The restore set is defined by role:

- the entry projects and every transitive `.csproj`/`.fsproj` in the graph
- the generated scoped `.sln`/`.slnx`
- the walk-up implicit files `Directory.Build.props`, `Directory.Build.targets`,
  `Directory.Packages.props`, `NuGet.config`, `global.json`
- `packages.lock.json` in any project directory in the graph

Excluded from the restore set, deliberately:

- **`.editorconfig`**, though it is in `ImplicitFilesIo.wellKnownFileNames`. Restore never reads it,
  and including it would invalidate the cached restore layer on every formatting-rule edit.
- everything else: sources, `appsettings.json`, rulesets, embedded resources.

`packages.lock.json` is included because locked-mode restore
(`RestorePackagesWithLockFile`) fails without it. It already reaches the file set through the SDK's
default `None` glob, so this is a routing decision, not new collection.

New pure module `Phase.fs` computes the partition from the already-resolved inputs.

Default output directory under `--restore` is `./<ProjectName>.restore`, so two invocations that both
omit `-o` do not collide.

## C. Missing files warn instead of failing

`FileResolution.resolveFiles` applies the `Resolvers.FileExists` check to item-derived paths, not
only to property-derived ones, and reports which paths it dropped. The CLI prints one warning per
skipped file, naming the path.

Property-derived paths continue to be dropped **silently**. The distinction is deliberate: a
property is a scalar that any SDK or props file may default to a path never meant to exist (a
generated path under `obj/`, say), so warning on those would be noise on projects that build fine.
Items are authored by someone, so a missing one is worth reporting.

Because filtering precedes mirror-root computation, an absent `../.dockerignore` also stops
inflating the output tree.

## D. Multiple entry projects

The positional argument becomes a list:

```
dotnet isolate src/Login/Login.csproj src/Login.Qs/Login.Qs.csproj -o /isolated
```

`ProjectGraph` gains a multi-root resolve. The project graph, resolved file set, mirror root and
scoped solution file are all computed over the union of the entry projects' closures. `-o` is
required when more than one project is given, since there is no single project name to default to.

A single entry project behaves exactly as today.

## E. README rewrite

Cut the README down substantially. The two current Docker patterns are removed: the classic-builder
guarantee is dropped (buildx only), and the `COPY . /src` isolate stage is replaced by a bind mount,
which keeps the whole context out of the stage's layers.

The README must explain the caching *mechanism*, not just show a recipe: `COPY --from=<stage>` is
keyed on the checksum of the copied bytes rather than on the producing stage's layer history, so the
isolate stage may rerun on every build while the layers below it still cache-hit. The restore phase
then splits the output at the point where the cache should hold: the restore half changes only when
a project file or a build-props file changes, so `RUN dotnet restore` survives ordinary source edits.

The documented pattern:

```dockerfile
# syntax=docker/dockerfile:1
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS isolate
RUN dotnet tool install -g dotnet-isolate
ENV PATH="$PATH:/root/.dotnet/tools"
RUN --mount=type=bind,target=/src \
    dotnet isolate /src/src/Service1/Service1.csproj --restore -o /isolated/restore
RUN --mount=type=bind,target=/src \
    dotnet isolate /src/src/Service1/Service1.csproj -o /isolated/full

FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src
COPY --from=isolate /isolated/restore /src
RUN dotnet restore
COPY --from=isolate /isolated/full /src
RUN dotnet build -c Release --no-restore
RUN dotnet publish src/Service1/Service1.csproj -c Release --no-build -o /app

FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS run
COPY --from=build /app /app
ENTRYPOINT ["dotnet", "/app/Service1.dll"]
```

Points the README must make about this file:

- The bind mount is read-only, which is all the tool needs — it reads the source and writes only to
  `/isolated`.
- Hardlinking across the bind mount fails, and `LinkStrategy.probe` detects that once upfront and
  falls back to copying. No configuration required.
- Paths inside the isolated tree are relative to the computed mirror root, which is not always the
  solution root. Run the tool once and look at the output before writing `COPY` and `publish` paths.

The existing "What about files outside the solution folder?" section is retained but tightened.

## Testing

**Unit** — paths built with `DotnetIsolate.UnitTests.PathHelpers`, never asserted as strings:

- `OutputSafety`: output equal to / above / below a source file; exclusion of paths under the output
  directory; a sibling directory whose name shares a prefix is not treated as inside.
- `Phase`: the restore partition includes project files, solution and implicit build files, includes
  `packages.lock.json`, and excludes `.editorconfig` and sources.
- `FileResolution`: missing item-derived paths are dropped and reported; missing property-derived
  paths are dropped and not reported.
- `ProjectGraph`: multi-root resolve returns the deduplicated union of two overlapping closures.

**Integration** — each reproduction from the problem statement:

- `-o <solution root>` fails, and the source tree is intact afterwards.
- `-o` inside a project directory succeeds twice in a row.
- A pre-populated output directory is merged into, and the stale entries are reported.
- `--clean` empties the output directory first.
- `--restore` produces exactly the restore set.
- Two entry projects produce the union, with a scoped solution containing both.
- A `None` item pointing at an absent file produces a warning, not a failure.

**E2E (buildx only)** — the gap in the current suite. `DockerCacheTests` today only changes a file
*outside* the isolated closure, which proves nothing about the restore phase. Add a test using the
two-phase Dockerfile above that changes a file **inside** the closure and asserts
`RUN dotnet restore` is still `CACHED` while the build layer reruns. The classic-builder theory
cases (`InlineData(false)`) are removed along with the classic-builder guarantee.

## Requirements to amend

`REQUIREMENTS.md` and `DESIGN.md` are the authoritative documents, so this work must update them
rather than silently diverge:

- **FR-7** ("If the output folder already exists, the tool deletes it and recreates it from
  scratch") is reversed: merging becomes the default, deletion moves behind `--clean`. Add the
  self-consumption error and the exclusion of the output directory from the input set.
- **DI-1** ("Two supported Dockerfile patterns are documented, both of which work under any
  builder") becomes one documented pattern, buildx only, using a bind mount and the restore phase.
- **QP-12** extends to cover a change *inside* the isolated closure, not only outside it, and drops
  the classic-builder half of its matrix.
- **REL-2** and **PR-1** are unchanged, but both are now load-bearing in new ways worth noting:
  REL-2's fallback is what makes the bind-mount pattern work, and PR-1 is what the two-invocation
  restore split trades against.
- New requirements for `--restore`, `--clean`, multiple entry projects, and warn-on-missing-file.

## Out of scope

- An `--exclude <glob>` flag. Considered for problem 3, unnecessary once the cause was identified as
  file absence.
- A published dotnet-isolate container image. The bind-mount pattern runs inside the Dockerfile, so
  the tool ships only as a NuGet tool.
- Following `<Import Project="..."/>`. Still an unfixed limitation, unchanged by this work.
