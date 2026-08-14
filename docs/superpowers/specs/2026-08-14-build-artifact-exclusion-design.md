# Excluding build artifacts from the isolated output, and collapsing the stale-entry warning

Date: 2026-08-14

Two problems reported from real use on a large solution. They are unrelated in cause but share the
same CLI reporting surface, so they are settled together.

## Problem statement

1. **Generated build artifacts are copied into the isolated output.** Running the tool on a real
   solution placed `obj/project.assets.json`, `obj/project.nuget.cache`,
   `obj/project.packagespec.json`, `obj/Debug/<tfm>/<Project>.AssemblyInfo.cs`,
   `obj/Debug/<tfm>/<Project>.AssemblyInfoInputs.cache` and parts of `bin/` into the output tree.
   None of them is a build input. Worse, they defeat the tool's own purpose: `project.assets.json`
   and `*.nuget.g.props` embed absolute *host* paths (`/home/<user>/.nuget/packages`) that are
   wrong inside the container, `obj/**` caches can make the container's build believe it is already
   up to date, and every host-side rebuild rewrites them — which changes the isolated output's
   bytes, changes the `COPY --from` cache key (DI-1), and reruns `dotnet restore` and everything
   below it even though no source changed. This is exactly the cache thrash the tool exists to
   prevent, and REL-1's determinism claim cannot hold on a developer machine while it persists.

2. **The stale-entry warning is one line per file.** FR-7 merges into an existing output directory
   and reports every entry the run did not itself produce. On a directory with prior contents this
   is hundreds of lines of console noise carrying one bit of information.

## Findings that constrain the design

Verified directly during design, against SDK 10.0.302; they contradict the obvious approaches.

**The SDK excludes only a project's *own* `bin`/`obj`.** `DefaultItemExcludes` is
`$(BaseOutputPath)/**;$(BaseIntermediateOutputPath)/**`, resolved relative to the project being
evaluated. A project whose directory contains *other* projects therefore globs the children's
artifacts in. Reproduced with a root-level `Root.csproj` above `App/` and `Lib/`:

```
Compile: App/obj/Debug/net8.0/App.AssemblyInfo.cs
         App/obj/Debug/net8.0/.NETCoreApp,Version=v8.0.AssemblyAttributes.cs
         Lib/obj/Debug/net8.0/Lib.AssemblyInfo.cs
None:    App/obj/project.assets.json, App/obj/App.csproj.nuget.dgspec.json,
         App/obj/Debug/net8.0/App.AssemblyInfoInputs.cache, App/bin/Debug/net8.0/App.dll, …
```

**A hand-written glob bypasses `DefaultItemExcludes` altogether.** Only the SDK's *default* item
globs carry the exclusions. A `<None Include="**\*"/>` or
`<Content Include="**\*.json" CopyToOutputDirectory="PreserveNewest"/>` authored in a `.csproj` or
`Directory.Build.props` collects `obj/` contents even for a project that contains no other project.
This is the more likely cause in the reporting repo, whose entry projects are leaves of the
dependency tree.

Both mechanisms produce the same *resolved paths*, which is why the rule below filters on the path
rather than on the item type, the defining project, or the glob that produced it. It is correct
regardless of how the file arrived.

**The SDK already excludes more than expected.** Dot-directories (`.git/`, `.vs/`, `.idea/`, via
`DefaultExcludesInProjectFolder`) and `*.user` files never appear in the resolved set. No rule is
needed for them.

**The SDK excludes none of these**, all of which came through as `None` items of the root project in
the same reproduction: `node_modules/**`, `TestResults/**`, `packages/**`, `*.binlog`,
`coverage.cobertura.xml`, and every unrelated source file in the subtree.

**Reading is not what moves a BuildKit cache key.** The filter runs on the resolved path list
*after* MSBuild evaluation, so the dropped files are never opened — the tool's read-set shrinks
rather than grows. The only added I/O is one `readdir` per candidate parent directory, all of which
MSBuild already walked. Whether BuildKit narrows a `RUN --mount=type=bind` cache key by the
process's actual read-set is **not verified**; the belief is that it digests the mounted subtree up
front, which would mean the isolate stage's own key still moves when `obj/` changes on the host.
That does not undermine this change — DI-1 already concedes the isolate stage reruns, and the
boundary that matters is the `COPY --from` below it — but it means a `.dockerignore` covering
`bin/` and `obj/` remains a complementary fix. See "Deferred" below.

## Design

### The rule (new requirement FR-15)

A resolved input is dropped when any of three rules matches. Rules A and B are path-segment rules;
C is a filename rule.

| Rule | Fires on a path segment / name | Anchor |
|---|---|---|
| A | `bin`, `obj`, or `TestResults` | the segment's parent directory contains a `*.csproj`, `*.fsproj` or `*.vbproj` |
| B | `node_modules` | none |
| C | `*.binlog`, `*.coverage`, `*.cobertura.xml`, `coverage.*.xml` | none |

Segment and name comparison is case-insensitive (`OrdinalIgnoreCase`), consistent with
`MirrorRoot.isUnder`.

**Why rule A is anchored.** The anchor is what makes it safe: `Web/bin/Debug/net8.0/Web.dll` is
dropped because `Web/` holds `Web.csproj`, while a checked-in `tools/bin/build.sh` or a test-fixture
directory named `TestResults/` with no project file beside it survives. `TestResults` folds into A
rather than standing alone precisely because it is created next to a test project.

**Why rule A is not derived from MSBuild.** Querying each project's `BaseIntermediateOutputPath`,
`BaseOutputPath` and `ArtifactsPath` would be exact for the projects in the closure, and blind to
every project outside it — which is the root-level-project case that produced the report. The
path-based rule covers both, and costs no additional MSBuild evaluation.

**Why `node_modules` is dropped at all.** `DESIGN.md` already excludes the `Analyzer` and
`Reference` item types on the grounds that they resolve into the SDK install and the NuGet cache,
"restored inside the container." `node_modules` is the same category: a package-manager restore
directory, reproducible from `package-lock.json`, restored in-container, and typically the single
largest source of cache thrash in a repo that has one. Rule B extends an existing principle rather
than introducing one.

**`packages/` is deliberately out of scope.** The legacy `packages.config` restore directory would
qualify on the same reasoning as `node_modules`, but in JS-style monorepos `packages/` holds
*source* (`packages/<name>/src/...`), so an unanchored rule is a live hazard. Anchoring it to the
legacy layout buys little — `packages.config` projects barely function under `dotnet msbuild`. It
can be added later, anchored, if a real repo needs it.

**Accepted collateral.** A file genuinely checked in under `<ProjectDir>/obj/` or
`<ProjectDir>/bin/` is dropped. This is documented in FR-15 rather than mitigated: those
directories are build output by universal convention, and the summary line below reports the count.

### Where the filter runs

In `Pipeline.isolate`, immediately after `allFiles` is assembled from project files plus implicit
files, and **before** `OutputSafety.partitionInputs` and the mirror-root computation. This is the
same position, for the same reason, as FR-14's missing-file drop: an artifact must not inflate the
mirror root (FR-2) nor count toward `OutputSafety.validate`'s "the output directory contains every
resolved input file" check.

### Modules

Following the existing pure/IO split (`ImplicitFiles.fs` / `ImplicitFilesIo.fs`, whose header
explains the coverlet rationale — QP-5):

- **`BuildArtifacts.fs`** (pure). `type ContainsProjectFile = string -> bool`;
  `isBuildArtifact : ContainsProjectFile -> string -> bool`; and
  `partition : ContainsProjectFile -> string list -> {| Kept: string list; Excluded: string list |}`.
  Fully unit-testable against a fake predicate, with no disk access.
- **`BuildArtifactsIo.fs`** (IO). The real `ContainsProjectFile`, memoized in a
  `ConcurrentDictionary<string, bool>` so each candidate parent directory is enumerated once — a
  large repo hits the same `obj/` parent hundreds of times.

`Pipeline.IsolateResult` gains `ExcludedArtifacts: string list`. The list rather than a count, so
tests can assert on membership; the CLI prints only the count.

### CLI output

Both changes replace per-file loops in `Program.fs`:

```
note: skipped 143 generated build artifact(s) (bin/, obj/, node_modules/, build and coverage logs)
warning: the output directory was not clean (12 pre-existing file(s) left in place); use --clean to replace it
```

`ExcludedUnderOutput` (FR-7) and `MissingFiles` (FR-14) keep their per-file warnings: both are rare,
and each individual path is separately actionable. Artifact exclusions and stale entries are neither
— they arrive in bulk and carry one bit of information each.

Both lines are suppressed entirely when their count is zero.

### Documentation changes

- **FR-15** — new, stating the rule, the anchor, the rationale, and the accepted collateral.
- **FR-7** — amended: "every entry the run did not itself produce is reported to the user" becomes a
  reported count.
- **DESIGN.md** — the file-resolution section gains the two mechanisms above (sibling glob and
  hand-written glob bypassing `DefaultItemExcludes`), since both are non-obvious and were verified.

## Testing

**Unit** (`BuildArtifactsTests.fs`, fake `ContainsProjectFile`):
- `App/obj/project.assets.json` excluded when `App/` has a project file; kept when it does not.
- `App/bin/Debug/net8.0/App.dll` excluded; `tools/bin/build.sh` kept (no project file beside it).
- A segment match deep in a path, and a match on the first segment.
- Case variance: `OBJ`, `Bin`, `TestResults` vs `testresults`.
- `node_modules/pkg/index.js` excluded with no anchor.
- `*.binlog` / `coverage.cobertura.xml` excluded; `notes.md` kept.
- Near-misses that must survive: `obj2/`, `binaries/`, `my.binlog.txt`.
- All paths built via `DotnetIsolate.UnitTests.PathHelpers.path` (POR-1).

**Integration:** build a fixture solution so real `obj/`/`bin/` exist, isolate it, and assert the
output tree contains no `obj` or `bin` directory and that the file count dropped accordingly.

**CLI:** the two summary lines appear with correct counts, and are absent at zero.

## Deferred

**Rule for foreign project directories.** A more aggressive generalisation — drop any resolved file
under a directory whose project file is not in the isolated closure — follows directly from FR-1's
"nothing more". It is deferred because it would break a deliberate cross-project link
(`<Compile Include="..\OtherProject\Shared.cs"/>`), which `FileResolution.fs` explicitly honours
today, in the narrow case where the linked file's owning project is outside the closure. Revisit if
a real repo shows unrelated project sources reaching the output.

**E2E verification of the bind-mount cache key.** `DotnetIsolate.E2ETests` already drives real
`docker build` runs and asserts cache-hit behaviour, so the unverified BuildKit claim above can be
pinned with a test rather than left as belief. Deferred to keep this change focused; it changes no
behaviour either way.
