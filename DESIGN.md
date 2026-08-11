# Design

This describes how dotnet-isolate is built to satisfy the requirements in REQUIREMENTS.md
(referenced by ID below).

## Solution layout

- **`DotnetIsolate.Core`** — the logic library: project graph discovery, file resolution,
  link-strategy detection, output materialization. Two of its modules are pure and exist to keep the
  pipeline honest: `OutputSafety.fs` (partitioning the resolved inputs against the output directory
  and rejecting an output directory that would consume them — FR-7) and `Phase.fs` (selecting the
  restore subset — FR-11). No console/CLI concerns live here, so it's independently testable.
- **`DotnetIsolate`** — the CLI entrypoint (Argu-based, QP-1), a thin wrapper over
  `DotnetIsolate.Core`.
- **`DotnetIsolate.UnitTests`** — fast, isolated tests of `DotnetIsolate.Core`'s internals (QP-2,
  QP-5).
- **`DotnetIsolate.IntegrationTests`** — exercises the whole tool end-to-end against a fixture
  solution on disk (QP-2, QP-3, QP-4).
- **`DotnetIsolate.E2ETests`** — drives real `docker build` runs and asserts cache-hit behavior on a
  second run (QP-12), including the documented DI-1 two-phase Dockerfile. Its other two fixtures
  (`COPY . /src` self-contained, and host-side) are no longer documented patterns but are kept as
  regression coverage of the `COPY --from` bridge itself. Requires a Docker daemon; not part of the
  coverage gates.

(Project/package names above are assumed for concreteness, not yet explicitly confirmed — see
"Open questions" at the bottom.)

## Pipeline

1. **Resolve the project graph.** Starting from the target `.csproj`, evaluate `ProjectReference`
   items via `dotnet msbuild -getItem:ProjectReference` (in practice, combined with step 2's item
   types into one call per project — see below) and walk the graph breadth-first, deduplicating by
   resolved absolute path — so a diamond dependency like `LogicCommon`, referenced by both
   `ServiceA` and `ServiceB` in the integration fixture, is only visited (and evaluated) once. This
   produces the full set of projects to include (FR-1, FR-4).

   Each BFS level's projects are evaluated *concurrently* rather than one at a time (`ProjectGraph.
   resolve`, `Array.Parallel.map` over the level's frontier) — a real `dotnet msbuild -getItem`
   invocation costs ~150-200ms of pure process/host-startup overhead regardless of project size
   (measured directly against a real 241-project solution), and most real solutions are wide but
   shallow, so this is what makes PR-1's O(d) (graph depth, not project count) bound achievable:
   `d` sequential rounds instead of `n` sequential calls. Verified this doesn't reintroduce
   duplicate evaluation of a shared dependency discovered by multiple siblings in the same level
   (`ProjectGraphTests.fs`'s call-counting tests assert every distinct project is resolved exactly
   once, including under that concurrent fan-in).

2. **Resolve each project's files.** For every project in that set, the same `dotnet msbuild`
   call from step 1 also requests every item type in `FileResolution.fileItemTypes` — the compile
   inputs (`Compile`, `Content`, `None`, `EmbeddedResource`), the analyzer inputs
   (`AdditionalFiles`, which is how StyleCop's `stylecop.json` reaches csc), the WPF ones (`Page`,
   `ApplicationDefinition`, `Resource`), and `TypeScriptCompile`. `Pipeline.fs` memoizes the
   combined per-project result so steps 1 and 2 share one MSBuild spawn instead of two. The project
   file itself (`.fsproj`/`.csproj`) is added explicitly — `-getItem` never returns it, since a
   project file isn't an item of itself, but it's obviously required to build the isolated output
   at all (found the hard way: an early version of this step silently produced an output tree with
   every source file but no project files, which only surfaced once the end-to-end pipeline test
   tried to actually build the result).

   Two item types are conspicuously *absent*, and were checked rather than assumed: `Analyzer`
   resolves into the SDK installation and `Reference` into the NuGet cache. Copying either would
   drag the mirror root (step 4) out of the repo entirely, and the container restores both for
   itself. `Reference` has a second problem — its file path lives in `HintPath` metadata, not in
   `FullPath`, which for a `Reference` is a meaningless project-dir-plus-assembly-name string. An
   in-tree `<Reference><HintPath>` is therefore a known gap, and a deliberate one for now.

   The same call also carries `-getProperty` (FR-9), because not every build-relevant input is an
   *item*. Analyzer setups are the motivating case: `stylecop.json` arrives as an `AdditionalFiles`
   item, but the `.ruleset` next to it is named by the `CodeAnalysisRuleSet` *property*, so no
   amount of `-getItem` querying would ever surface it — and a missing ruleset doesn't degrade the
   isolated build, it fails it outright. `AssemblyOriginatorKeyFile`, `ApplicationIcon`,
   `ApplicationManifest`, `Win32Resource`, and `Win32Manifest` are the same shape. `-getItem` and
   `-getProperty` combine in a single `dotnet msbuild` invocation
   (`MsBuild.getItemsAndProperties`), so this costs no extra process spawns and leaves PR-1's bound
   untouched.

   Properties differ from items in two respects that matter here. First, `-getProperty` returns the
   raw evaluated string, not resolved `FullPath` metadata, so a relative value like
   `..\..\analysis.ruleset` is made absolute against the project's own directory
   (`FileResolution.resolvePropertyFiles`). Second, a property is a lone scalar that any SDK or
   props file may default to something never meant to be read, so values naming no existing file
   are dropped rather than failing the whole run at materialization. Only properties naming build
   *inputs* belong in the list at all — `DocumentationFile` is deliberately excluded, since it
   names a file the compiler writes rather than reads.

   `EditorConfigFiles` is handled separately again (`ancestorGlobbedItemTypes`, FR-10) because
   MSBuild *auto-discovers* it by climbing the directory tree, and that climb doesn't stop at the
   repo: a stray `~/.editorconfig` without `root = true` resolves for every project, and copying it
   in would pull the mirror root up to the home directory. These items are therefore filtered to
   the solution root — the same ceiling FR-5's walk-up already uses — which is why step 3's
   solution discovery runs *before* this step in `Pipeline.isolate` (it needs no MSBuild evaluation
   of its own, so the reordering is free). One wrinkle found by testing rather than assumption: the
   C# SDK populates `EditorConfigFiles` at evaluation time but the F# SDK leaves it empty, so
   `.editorconfig` is *also* carried by name in FR-5's language-agnostic walk-up. The item query
   still earns its place — it finds `.editorconfig` files nested *inside* a project, which a
   walk-up from the project directory never looks at.

   All of this is what makes FR-4 correct in the face of SDK-style implicit globs,
   `Directory.Build.props`-injected items, and conditions, without hand-replicating MSBuild's
   globbing/condition semantics (an alternative that was considered and rejected — see below).
   Using the `-getItem` CLI surface (available since the .NET 8 SDK) instead of embedding
   `Microsoft.Build` in-process avoids MSBuildLocator/assembly-loading complexity while still using
   real evaluation. Even with the level-parallel BFS above, this per-process overhead is what PR-1's
   bound is ultimately paying for; if O(d) rounds still isn't fast enough in practice (a very deep
   graph, or heavy contention from many concurrent `dotnet` processes on a low-core machine), the
   in-process `Microsoft.Build` API remains the fallback to revisit, since it would eliminate the
   per-call process-spawn cost entirely.

3. **Locate the solution root, then resolve implicit repo-level files.** If `-s`/`--solution` was
   given, use it directly. Otherwise, walk up from the target project's directory to the first
   ancestor directory containing a `.sln`/`.slnx` file — that's the solution root, used here as a
   walk-up ceiling and again in step 7 as FR-3's template (FR-8). Either way, log which solution
   file is being used to the console (explicit or auto-discovered), since more than one solution
   can reference the same project and the choice isn't always obvious. If none is found before the
   filesystem root (and none was given explicitly), there's no solution to use as a ceiling or a
   template: skip step 7 entirely and let this step's walk-up run to the filesystem root instead.

   Then, for every project directory found in step 1, walk upward through parent directories
   collecting `Directory.Build.props`, `Directory.Build.targets`, `Directory.Packages.props`,
   `NuGet.config`, `global.json`, and `.editorconfig` wherever they occur, up to that ceiling
   (FR-5). Every occurrence is collected, not just the nearest, since `Directory.Build.props`
   chains commonly import further-up parents explicitly.

   Note the ordering in `Pipeline.isolate`: the solution-discovery half of this step runs *before*
   step 2, which needs the ceiling for its ancestor-globbed items. Only the walk-up half genuinely
   comes after step 1.

4. **Compute the mirror root.** The common ancestor of every path collected in steps 1–3 becomes
   the root that gets mirrored into the output folder (FR-2). Nothing above it is copied. The
   solution root directory from step 3 (if any) is included as one of the inputs here even when it
   contains no resolved files of its own - otherwise the mirror root could end up *below* the
   solution root, and step 7's generated solution file would need to be written outside the output
   folder to preserve its original relative position. Including it guarantees the mirror root is
   always at or above the solution root, so the generated solution file always lands inside the
   output folder as FR-3 requires.

5. **Decide the link strategy.** For each distinct (source root, destination root) pair —
   normally just one, since it's rare for a solution's projects to span drives/volumes — pick one
   real file already resolved from that source root, attempt a hardlink of it to a throwaway name
   under the destination root, and use the result to decide hardlink-or-copy for every file under
   that pair (REL-2). This is one deliberate upfront check per pair, not a try/catch wrapped around
   every file copy — and deliberately never writes anything into the source tree (the destination
   is ours to manage; the source is the user's actual solution).

6. **Materialize the output folder.** Place every resolved file at its mirrored relative path using
   the strategy chosen in step 5. Materialization no longer deletes anything: it merges over
   whatever is already there and returns the entries it found that this run did not produce, which
   the CLI reports (FR-7). `--clean` (FR-12) reinstates delete-and-recreate. One consequence worth
   recording, because it only shows up on a second run: a hardlink at a path that already has an
   entry fails with `EEXIST` rather than replacing it (unlike `File.Copy`), so the existing entry is
   removed first — guarded against the destination being the source file itself.

   Two things happen *before* this step and before the mirror root of step 4, not here, because both
   have to precede any filesystem mutation: every resolved input under the output directory is
   partitioned out of the input set (`OutputSafety.partitionInputs`), and the run fails outright if
   that leaves nothing (`OutputSafety.validate`). The order is what makes them work. Pointing `-o` at
   the solution root previously deleted the whole source tree and *then* failed; pointing it inside a
   project directory previously succeeded once and failed on the second run, because the SDK's
   default globs collected the first run's output as `Compile` items of the enclosing project and the
   tool tried to place the output inside itself. Excluding those inputs before the mirror root is
   computed also stops them dragging it outward.

   When `--restore` is set, `Phase.restoreSubset` narrows the placed set to the restore inputs
   (FR-11) *after* step 4 has computed the mirror root from the full set. Deriving the root from the
   full set in both phases is what makes the two outputs overlay exactly, so a Dockerfile can `COPY`
   the restore half, `RUN dotnet restore`, then `COPY` the full half straight over it.

7. **Generate the scoped solution file** (skipped if step 3 found no solution root, per FR-8).
   Parse the source solution as a template, keep only the
   entries whose target is in the included-project set from step 1, drop everything else (e.g. a
   `/Docs/` solution folder, unrelated projects), and write it — in the *same format as the source*
   (`.sln` or `.slnx`) — into the output folder at the mirrored path the original solution file
   occupied (FR-3). This is what lets a bare `dotnet build`/`dotnet restore` at the output root work
   with no extra arguments.

   This step runs in *both* phases, `--restore` included: the solution file is generated rather than
   resolved, so it never appears in step 6's file set at all and `Phase.restoreSubset` has nothing to
   select — while `dotnet restore` at the output root needs it.

   **Implementation status:** `SolutionFile.filterSln` (in `DotnetIsolate.Core`) implements this for
   classic `.sln` — parses the `Project(...)`...`EndProject` blocks and the `Global` section against
   the real structure `dotnet sln add` produces (verified directly, not guessed), filters both by
   resolved project path, and is dogfooded against this repo's own real `.sln` in the integration
   suite (the filtered output is written to disk and actually built with `dotnet build`, not just
   checked as text). `SolutionFileXml.filterSlnx` implements the same FR-3 contract for `.slnx`
   (XML): keeps only `<Project>` elements whose resolved `Path` is included, recursively drops any
   `<Folder>` left empty afterward, and writes without a BOM (verified directly: real `.slnx` files
   don't carry one, unlike `.sln`).

   **`.slnx` SDK caveat:** `.slnx` parsing requires a fairly recent SDK (verified directly: .NET 8
   SDK 8.0.423 fails on it outright with `MSB4068`; .NET 10 SDK 10.0.302 handles it fine). This
   isn't something isolation introduces — a `.slnx`-based source solution already needs an
   `.slnx`-capable SDK to build, isolated or not — so the tool preserves the source format rather
   than silently downgrading it. It does mean: if your source solution is `.slnx`, whatever builds
   the isolated output (host SDK, or the SDK image tag in a Dockerfile) needs to support it too —
   e.g. `sdk:10.0` rather than `sdk:8.0` in the pattern under DI-1. This repo's own solution file
   was briefly `.slnx` during early scaffolding, hit this exact SDK 8 build failure locally, and was
   reverted to `.sln` — a case of the source solution's format choice, not a tool requirement.

## Alternatives considered

- **Flatten the output + rewrite `ProjectReference` paths.** Would let the target project sit at
  the output root directly. Rejected: it requires parsing and mutating every copied `.csproj`,
  meaning those files could no longer be pure hardlinks of the originals. The mirrored-tree +
  generated-solution-file approach needs no rewriting at all and keeps every file — including
  project files — a true hardlink where the filesystem allows it.
- **Hand-rolled MSBuild glob/condition parser.** Would avoid shelling out to `dotnet msbuild`
  entirely, but risks silently diverging from real MSBuild behavior on `Directory.Build.props`
  -injected items or unusual conditions. Rejected in favor of real MSBuild evaluation via
  `-getItem`.
- **Per-file try/catch to decide hardlink vs. copy.** Simple to write but uses exceptions as
  control flow across potentially thousands of files. Rejected in favor of the single upfront
  capability probe (pipeline step 5).

## Link strategy detail (REL-2)

Given a set of files to place under a destination root:

1. Group files by their resolved source root (usually one group).
2. For each (source root, destination root) pair not yet decided: pick one real, already-resolved
   file from that source root, attempt to hardlink it to a throwaway name under the destination
   root, and record success/failure. Delete the throwaway file afterward, regardless of outcome.
   Nothing is ever written into the source tree.
3. Apply that pair's decision (hardlink or copy) to every real file in the group. If the probe
   file itself was successfully hardlinked, that's one file already placed correctly — no need to
   place it again.

This works identically on Windows (NTFS/ReFS support hardlinks the same way Linux/macOS
filesystems do), rather than branching on OS — the original "hardlink on Linux, copy on Windows"
framing undersold what NTFS can actually do, and OS-based branching would also miss the case of a
Linux/macOS output directory living on a different volume/filesystem (e.g. a FAT32 USB drive, a
network mount) than the source.

## Docker integration pattern (DI-1)

Within a single build *stage*, both classic Docker and BuildKit chain `RUN`/`COPY` cache keys off
the previous instruction's result: `COPY <entire solution> .` hashes the whole copied tree, so
touching one unrelated file anywhere in the solution changes that step's cache key — and everything
chained after it *in the same stage* (`RUN dotnet isolate ...`, `RUN dotnet restore`, ...) misses
cache as a consequence, regardless of REL-1's determinism guarantee about `dotnet isolate`'s own
output.

The mechanism that sidesteps this is `COPY --from=<stage>` between build stages. Docker caches a
multi-stage `COPY --from` by checksumming the actual bytes being copied from the source stage, not by
chaining off that stage's own (possibly cache-missed) layer history. So: run `dotnet isolate` in its
own dedicated stage, and bridge into the stage that does the expensive work
(`restore`/`build`/`publish`) with `COPY --from=isolate`. That copy — and everything chained after it
— cache-hits whenever `dotnet isolate`'s output is unchanged (REL-1), because the
`RUN dotnet isolate ...` step and everything before it being cache-missed stops mattering once the
bridge is a content-checksummed `COPY --from` rather than a same-stage chain.

**Verified: that checksum is mtime-insensitive.** Checked directly against buildx 0.36.0 / Docker
29.7.1 rather than assumed — a stage was forced to rerun, one of its output files had its mtime moved
to 2030 with its content unchanged, and a second file's content was changed. The `COPY --from` of the
unchanged-content file was `CACHED`, as was the expensive `RUN` between the two `COPY`s; only the
`COPY` of the changed file reran. This is load-bearing rather than incidental: the tool hardlinks
where it can (REL-2), so output files inherit their source's mtime, and a fresh CI clone stamps every
file with a new one. Had mtime counted, none of this would work and the restore split below would buy
nothing.

**The restore split.** `COPY --from` puts the cache boundary between stages; `--restore` (FR-11) puts
it in the right *place*. The isolate stage runs the tool twice — once with `--restore`, once without —
and the build stage copies the restore half, runs `dotnet restore`, then copies the full half over it
(both halves share a mirror root, per pipeline step 6, so they overlay exactly). The restore half
contains no sources, so an ordinary source edit leaves it byte-identical and `RUN dotnet restore`
stays cached. This is the one property the whole feature rests on, so QP-12 asserts it directly: an
E2E test changes a file *inside* the isolated closure and requires `dotnet restore` to be `CACHED`
while the build layer reruns.

**Bind mount, not `COPY . /src`.** The source reaches the isolate stage through
`RUN --mount=type=bind,target=/src`. `COPY . /src` would pull the entire build context into that
stage's layers — precisely the cost the tool exists to avoid — whereas the mount is read-only and
transient, which is all the tool needs: it reads the source and writes only to its output directory.
The price is buildx: the classic builder has no bind mounts, so DI-1's earlier "works under any
builder" guarantee is dropped, along with the two patterns that carried it (the `COPY . /src`
self-contained one and the host-side one). Hardlinking across the mount fails, and REL-2's upfront
`LinkStrategy.probe` is what quietly absorbs that — it detects the failure once and copies instead,
with no configuration. REL-2 was written for FAT32 sticks and network mounts; it is now what makes
the documented Docker pattern work at all.

(A dedicated `dotnet-isolate` Docker image was considered as a way to make the isolate stage
cheaper/more pinned, and dropped: the `RUN dotnet tool install` step sits before anything that
depends on the source, so it is cached across builds on its own, and the isolate stage can reuse the
same SDK base image the build stage needs anyway — a separate published image bought no caching
benefit, only minor polish, for a maintenance cost not worth it right now.)

## CI / release pipeline (QP-4, QP-5, QP-7, QP-9)

Implemented at `.github/workflows/build.yml`, three jobs:

- **`test`** — the `{windows, ubuntu, macos} × {net8, net10 SDK}` matrix (POR-1/POR-2/QP-9). The
  net10 leg sets `DOTNET_ROLL_FORWARD=LatestMajor` rather than mutating `global.json` in CI, to
  get the SDK-8-pinned repo actually running under the .NET 10 SDK — verified locally (including
  a full test-suite pass under it) before committing to that approach.
- **`coverage`** — runs once (not per matrix leg, to avoid 6x redundant coverage collection),
  computes both coverage numbers using the two `.runsettings` files, and gates on QP-4/QP-5 via
  `.github/scripts/check-coverage.py` — a standalone, locally-testable script (verified against
  both a passing and a synthetically-failing threshold before trusting it in CI) rather than
  inline YAML.
- **`publish`** — needs both other jobs green; runs on pushes to `main` (green commit → prerelease
  per QP-7) or a `v<major>.<minor>` tag (→ clean stable release, same job, `dotnet pack` picks up
  whichever version Nerdbank.GitVersioning computes for that ref). Uses NuGet Trusted Publishing
  (`NuGet/login@v1`, OIDC token exchange, no stored API key) — the nuget.org policy is already
  configured for repository `GuckesThonfeldGuckesGbr/dotnet-isolate`, workflow `build.yml`,
  environment `main`; the job's `environment: main` must keep matching that exactly, or the OIDC
  exchange fails.

**Outstanding manual step (not something this design doc or the workflow file can do):** the
`publish` job reads `secrets.NUGET_USER` (your nuget.org profile name, e.g. `ctg`, per the Trusted
Publishing docs' recommendation of a secret over hardcoding it) — needs adding as a GitHub Actions
repository secret before the first real publish will succeed.

- Coverage is collected per test project via `coverlet` and reported separately (QP-6) — no merge
  step.
- Commit signing (QP-8) is a one-time local/environment setup task, not covered further here.

### Why unit coverage needs a different scope than integration coverage

Measuring QP-5's 95% threshold against the *whole* `DotnetIsolate.Core` assembly is structurally
unreachable by design: a meaningful fraction of Core is IO-touching orchestration (MSBuild process
invocation, P/Invoke hardlinks, real filesystem mutation, pipeline composition) that unit tests
deliberately don't exercise — that's what the integration suite is for. Measured directly: unit
tests alone cover ~46% of the whole assembly; integration tests alone cover ~90%.

The fix is **not** `[<ExcludeFromCodeCoverage>]` on that IO-touching code — tested that first and
confirmed directly that the attribute is baked into the assembly and excludes the tagged code from
*every* coverage run against it, including the integration one, which would make QP-4 blind to
exactly the code it exists to validate.

Instead:
- Each of `MsBuild.fs`, `Hardlink.fs`, `Materialize.fs`, `Pipeline.fs`, `LinkStrategy.fs` is
  entirely IO-touching, so it's its own file/class already.
- Three modules mix pure logic with one small IO-touching resolver function each
  (`FileResolution.projectItemsResolver`, `ImplicitFiles.filesOnDisk`,
  `SolutionDiscovery.solutionFilesOnDisk`). coverlet's exclude filter only works at whole-class
  granularity, so each of those three functions (plus the data it privately depends on) was moved
  into its own file — `FileResolutionIo.fs`, `ImplicitFilesIo.fs`, `SolutionDiscoveryIo.fs` — so
  it could be targeted by class name without dragging its sibling pure logic down with it.
- `DotnetIsolate.UnitTests/coverage.unit.runsettings` excludes all of the above (plus F#'s
  `StartupCode$*` module-init compiler noise, which coverlet can't meaningfully attribute to a
  test either) via a **per-invocation** coverlet filter, not an assembly attribute.
- `DotnetIsolate.IntegrationTests/coverage.integration.runsettings` excludes only the compiler
  noise, measuring the whole assembly otherwise — confirmed this lands at ~92%, comfortably above
  QP-4's 90% (one point of that gap is permanent and expected: the Windows `CreateHardLinkW`
  P/Invoke branch in `Hardlink.fs` can never be hit when coverage is measured on a Linux CI
  runner).

Net effect, measured directly after wiring this up: unit coverage of its in-scope code is ~99%;
integration coverage of the whole assembly is ~92%. Both clear their thresholds with real margin,
not by accident of measurement.

## Test fixture (QP-3)

Two sample solutions live under `src/TestSolutions/`, both with the same five-project shape:

    ServiceA -> LogicA, LogicCommon
    ServiceB -> LogicB -> LogicCommon

`LogicCommon` being shared by both services (directly for ServiceA, transitively via LogicB for
ServiceB) exercises the dedup logic in pipeline step 1, and the fixture's small, fully-known shape
is what QP-3's "cover the complete logic" expectation leans on. `LogicCommon` also declares
`credentials.json` as a real `None` item and explicitly `<None Remove>`s a stray `.env.local` file
that sits in the same directory but isn't referenced by any MSBuild item - proving file selection
is driven by real MSBuild evaluation, not naive directory copying.

- `src/TestSolutions/DiamondWithIncludedFiles/` — `.slnx`, projects target `net10.0` (needed to
  dogfood `.slnx` filtering at all; see the SDK caveat above). Integration tests against it skip
  gracefully on SDKs below .NET 10.
- `src/TestSolutions/DiamondWithIncludedFilesSln/` — classic `.sln`, projects target `net8.0`, so
  it exercises `.sln` filtering unconditionally on every CI leg without any SDK gate.

## Open questions (not blocking, flag for review before/while implementing)

- Project names: `DotnetIsolate.Core`, `DotnetIsolate`, `DotnetIsolate.UnitTests`,
  `DotnetIsolate.IntegrationTests`, `DotnetIsolate.E2ETests` — assumed, not explicitly confirmed.

**Resolved:** NuGet package id / tool command name — `dotnet-isolate` (confirmed available on
nuget.org, packed and smoke-tested locally as a real global tool: `dotnet isolate` resolves and
runs correctly once installed). Versioning — Nerdbank.GitVersioning, `version.json` at the repo
root with `"version": "0.1"` and `publicReleaseRefSpec: ["^refs/tags/v\\d+\\.\\d+"]` (empirically
verified locally, not just configured — a non-tag build produces `0.1.<height>-g<shorthash>`, a
build at a matching tag produces a clean `0.1.<height>` with no prerelease component; see QP-7).
Starting minor version `0.1`, so the first stable tag should be `v0.1` — NBGV fills in the patch
component from git commit height automatically, so the exact first stable version number (e.g.
`0.1.47`) depends on how many commits precede that tag, not a fixed `0.1.0`. Publishing — NuGet
Trusted Publishing (OIDC, no stored API key); policy already configured on nuget.org: repository
`GuckesThonfeldGuckesGbr/dotnet-isolate`, workflow `build.yml`, GitHub environment `main`.
