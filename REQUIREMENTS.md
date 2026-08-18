# Requirements

Numbered so they can be referenced elsewhere (DESIGN.md, tests, PR descriptions) — e.g. `FR-3`,
`POR-1`.

## Functional

- **FR-1** — Given a path to a `.csproj`, the tool produces an output folder containing exactly
  that project and the transitive closure of its `ProjectReference` dependencies — nothing more.
- **FR-2** — The output folder mirrors the original relative directory structure of every included
  file, rooted at the common ancestor shared by all included projects and repo-level build files
  (see FR-5). No file paths are rewritten.
- **FR-3** — The tool generates a new solution file inside the output folder (same format as the
  source solution — `.sln` or `.slnx`), derived from the source solution as a template but
  containing only the included projects. This lets `dotnet build`/`dotnet restore` run at the
  output root with no arguments, provided the consuming SDK supports that format — see DESIGN.md
  for a caveat specific to `.slnx`. **Status:** both `.sln` and `.slnx` filtering are implemented.
- **FR-4** — The tool includes every included project's own project file (`.fsproj`/`.csproj`)
  plus every file its `Compile`, `Content`, `None`, `EmbeddedResource`, `AdditionalFiles`, `Page`,
  `ApplicationDefinition`, `Resource`, and `TypeScriptCompile` MSBuild items resolve to (via real
  MSBuild evaluation — see DESIGN.md), plus `ProjectReference` targets, recursively. These paths
  are taken at face value even when they point outside the solution tree, since every one of them
  was authored by hand. The project file itself is included explicitly, not via an MSBuild item
  type — `dotnet msbuild -getItem` never returns it, since a project file isn't an item of itself.
  `Analyzer` and `Reference` are deliberately excluded: they resolve into the SDK install and the
  NuGet cache, which the container restores for itself.
- **FR-5** — The tool walks up from each included project's directory and includes any
  `Directory.Build.props`, `Directory.Build.targets`, `Directory.Packages.props`, `NuGet.config`,
  `global.json`, and `.editorconfig` files it finds along the way, since MSBuild implicitly
  consumes these during restore/build even though they're never referenced explicitly by a project.
- **FR-6** — The output location defaults to `./<ProjectName>` — or `./<ProjectName>.restore` under
  `--restore` (FR-11), so the two phases of one project don't collide — and is overridable with
  `-o`/`--output-dir`, which accepts any relative or absolute path (not just a plain name created
  inside the current directory). With more than one entry project (FR-13) there is no single project
  name to default to, so `-o`/`--output-dir` is required rather than guessed.
- **FR-7** — If the output folder already exists, the tool merges into it: files are placed over
  whatever is there, nothing is deleted, and the number of entries the run did not itself produce
  is reported to the user (a count, not a list — a merge into a populated directory otherwise emits
  hundreds of lines carrying one bit of information). `--clean` (FR-12) restores delete-and-recreate for callers who need the output to
  contain exactly the isolated set. Deletion was previously the default and was reversed because it
  is destructive: with `-o` pointing at the solution root, the tool deleted the entire source tree
  and only then failed. Two further guards follow from the same incident — before any filesystem
  mutation the tool fails when the output directory contains every resolved input file, naming it
  and explaining that it would consume its own inputs; and every resolved input that lives under the
  output directory is excluded from the input set (and reported), since a previous run's output is
  indistinguishable from source to MSBuild's default globs and would otherwise be materialized
  inside itself, one level deeper each run.
- **FR-8** — By default, the tool locates its "source solution" (used as FR-3's template and
  FR-5's walk-up ceiling) by walking up from the target project's directory to the first ancestor
  directory containing a `.sln`/`.slnx` file. Since more than one solution can reference the same
  project, an optional `-s`/`--solution` argument lets the user specify exactly which solution
  file to use, overriding auto-discovery. Whichever solution file ends up being used — explicit or
  auto-discovered — is logged to the console, so the user can see which one was picked. If no
  solution file is found (and none was explicitly given) before the filesystem root, FR-3's
  solution generation is skipped and FR-5's walk-up runs all the way to the filesystem root
  instead.

- **FR-9** — Not every build-relevant input is an MSBuild *item*: some are named by a *property*
  whose value is a path, and `-getItem` can never see them. The tool additionally includes the
  files named by each project's `CodeAnalysisRuleSet` (the `.ruleset` analyzers read),
  `AssemblyOriginatorKeyFile`, `ApplicationIcon`, `ApplicationManifest`, `Win32Resource`, and
  `Win32Manifest` properties, each resolved relative to the project's own directory when the
  value is relative. A property whose value names no existing file is skipped, since an SDK is
  free to default one to a path that was never meant to be read. Only properties naming build
  *inputs* qualify — `DocumentationFile`, for instance, names a file the compiler *writes*, so it
  is deliberately not included.
- **FR-10** — Some MSBuild items are *auto-discovered* by globbing up the directory tree rather
  than authored, and their walk has no natural stopping point: `EditorConfigFiles` resolves a
  `.editorconfig` from the user's home directory as readily as from the repo. The tool includes
  these too, but bounds them by the solution root (FR-8) — the same ceiling FR-5 already uses —
  so an out-of-repo file can never be copied in and drag the mirror root (FR-2) out with it.
- **FR-11** — `--restore` emits only the files `dotnet restore` needs in order to evaluate the
  project graph: the included project files, the generated scoped solution (FR-3), the walk-up
  implicit files `Directory.Build.props`, `Directory.Build.targets`, `Directory.Packages.props`,
  `NuGet.config` and `global.json`, and `packages.lock.json` (without which a locked-mode restore
  fails). `.editorconfig` is deliberately excluded even though FR-5 collects it: restore never reads
  it, so including it would invalidate the cached restore layer on every formatting-rule edit —
  which is the one thing this flag exists to prevent (DI-1). Sources, `appsettings.json`, rulesets
  and embedded resources are excluded for the same reason. Without the flag, behaviour is unchanged
  and the complete set is emitted; producing both halves means two invocations into two output
  directories, which evaluates MSBuild twice. That cost against PR-1 is accepted deliberately, in
  exchange for a smaller flag surface than a single invocation emitting both halves. Both phases
  compute the mirror root from the *full* resolved set, so the two outputs overlay exactly and the
  restore half can be `COPY`d and then overwritten by the full half.
- **FR-12** — `--clean` deletes and recreates the output directory before writing, restoring the
  behaviour FR-7 used to have by default. Because it deletes, it demands more of the output
  directory than the default merge mode does: as well as FR-7's self-consumption check, a run with
  `--clean` is refused outright when the output directory contains *any* resolved input file, naming
  the directory and one of the inputs it would have destroyed. FR-7's check alone is not sufficient
  here — it only fires on *total* overlap, so `--clean -o src/Shared`, where `src/Shared` holds real
  sources while other inputs live elsewhere, would otherwise pass validation and then be deleted.
  Merge mode still tolerates that partial overlap, since its worst case is an incomplete output tree
  plus a warning rather than a deletion. Both checks run before any filesystem mutation, so `--clean`
  can never be the thing that deletes a source tree.
- **FR-13** — The tool accepts one or more entry projects in a single run. The project graph,
  resolved file set, mirror root and generated solution file are all computed over the union of
  their closures, deduplicated. A single entry project behaves exactly as before. Solution discovery
  (FR-8) walks up from the *first* entry project only, so an entry project that does not live under
  the discovered solution's directory is reported as a warning: its files are still isolated, but
  the generated solution file is filtered from the source solution and can only contain projects
  that were already members of it.
- **FR-14** — A path that a project's MSBuild *items* resolve to but which does not exist on disk is
  skipped, and each such path is reported to the user, rather than failing the run at
  materialization. The motivating case: a `.csproj` declaring `<None Include="..\.dockerignore"/>`
  where the `.dockerignore` excludes itself from the Docker build context — MSBuild still reports
  the item, since item resolution never consults the disk. Filtering happens before the mirror root
  is computed (FR-2), so an absent out-of-tree file also stops inflating the output tree.
  Property-derived paths (FR-9) continue to be dropped *silently*: a property is a scalar any SDK
  may default to a path never meant to exist, whereas an item was authored by someone and a missing
  one is worth reporting.
- **FR-15** — Generated build artifacts are excluded from the resolved set, because they are not
  build inputs and actively defeat the tool's purpose: `project.assets.json` and `*.nuget.g.props`
  embed absolute *host* paths that are wrong inside a container, `obj/**` caches can make the
  container's build believe it is already up to date, and every host-side rebuild rewrites them —
  changing the isolated output's bytes, changing the `COPY --from` cache key (DI-1), and rerunning
  restore for no source change. On a fresh CI clone none of this exists, which is why it shows up
  as a developer-machine problem. Four rules, applied to resolved paths before the mirror root is
  computed (FR-2) so no artifact can inflate it:
  1. a `bin`, `obj` or `TestResults` directory *whose parent holds a project file* — the anchor is
     what makes the rule safe, keeping a checked-in `tools/bin/build.sh` out of it;
  2. a `node_modules` directory anywhere, on the same reasoning that excludes the `Analyzer` and
     `Reference` item types (FR-4): it is restored inside the container from a manifest;
  3. `*.binlog`, `*.coverage`, `*.cobertura.xml` and `coverage.*.xml`;
  4. anything under a directory MSBuild itself declared as output via `ArtifactsPath`,
     `BaseOutputPath` or `BaseIntermediateOutputPath`.

  Rules 1 and 4 are both needed because they are blind in opposite directions. Rule 1 cannot see
  output relocated by `UseArtifactsOutput`, whose `bin`/`obj` sit under a repo-root `artifacts/`
  with no project file beside them; rule 4 can only name the output directories of projects in the
  closure, while the artifacts that leak most often belong to projects outside it — and
  `ArtifactsPath` is repo-wide, so reading it from a closure project covers the others too.
  A file genuinely checked in under a project's own `bin`/`obj` is dropped; this is accepted, and
  the number of exclusions is reported. The count, not the list: exclusions arrive in bulk.

- **FR-16** — A resolved input whose *owning project directory* — its nearest ancestor directory
  directly containing a `.csproj`/`.fsproj`/`.vbproj` — is not one of the closure's own project
  directories is **reported, and kept**. This is the residue of FR-1's "nothing more": a
  hand-written glob such as `<Compile Include="..\**\*.cs"/>` sweeps in files another project owns,
  which inflate the mirror root and move the `COPY --from` cache key whenever they change.

  It is reported rather than dropped because, unlike every FR-15 artifact, such a file is an input
  the build *consumes*: dropping a `Compile` item breaks compilation inside the container, far from
  its cause. And the tool cannot tell an accidental sweep from a deliberate
  `<Compile Include="..\OtherProject\Shared.cs"/>` link — after MSBuild evaluation both are bare
  paths, and FR-4 honours such links on purpose. The author holds the discriminator the tool lacks,
  so the ambiguity is surfaced rather than guessed, exactly as FR-14 surfaces a missing item file
  instead of failing. A file under a directory *no* project owns (`..\..\shared\Version.cs`) is
  never reported: that is the common, legitimate case, and warning about it would train users to
  ignore the note. Reported per owning directory with a count, since one glob sweeps many files.

## Performance

- **PR-1** — The analysis phase (determining the full set of files/projects to include) issues
  O(d) sequential rounds of external MSBuild evaluation, where d is the depth of the longest
  project-reference chain from the target project — not O(n) rounds, where n is the total project
  count. Projects at the same reference depth are evaluated concurrently, so a wide-but-shallow
  solution (many projects, few reference levels) analyzes in a small, bounded number of rounds
  regardless of its size. No absolute wall-clock threshold is specified, since that depends on
  machine parallelism, per-process MSBuild startup overhead, and disk/OS factors outside this
  tool's control; `DotnetIsolate.PerformanceTests` exists to catch a regression back toward O(n)
  scaling (e.g. a shared dependency getting re-evaluated once per incoming reference instead of
  once total), not to gate on a fixed number.

## Reliability / Determinism

- **REL-1** — Running the tool twice on an unchanged input produces an identical output file set
  with identical file contents, so Docker layer caching stays valid.
- **REL-2** — File placement uses hardlinks where the filesystem supports it, falling back to
  copying only where it doesn't. The decision is made via a single upfront capability probe per
  (source root, destination root) pair, not per-file exception handling. See DESIGN.md for the
  algorithm.

## Docker Integration

- **DI-1** — Exactly one Dockerfile pattern is documented, and it requires buildx (the default
  builder in current Docker); the previously-claimed classic-builder guarantee is dropped along with
  the two patterns that carried it. `dotnet isolate` runs in its own build stage, with the source
  reaching it through a **read-only bind mount** rather than `COPY . /src` — the tool only reads the
  source and writes to its output directory, and the mount keeps the whole build context out of that
  stage's layers, which is the cost the tool exists to avoid. The stage runs the tool **twice**:
  once with `--restore` (FR-11) and once without, into two output directories. The build stage then
  bridges to them with `COPY --from=<stage>`, whose cache key is the checksum of the copied bytes
  rather than the producing stage's own layer history — so the isolate stage may rerun on every
  build, as it must, while the layers below still cache-hit. Copying the restore half, running
  `dotnet restore`, then copying the full half over it puts the cache boundary exactly where a
  source edit does not cross it. See DESIGN.md ("Docker integration pattern") for the mechanism and
  the verified finding that this checksum is mtime-insensitive, which is what makes it survive
  hardlinked output and fresh CI clones.
- **DI-2** — The pattern does not require modifying the original solution/repo.
- **DI-3** — No dedicated `dotnet-isolate` Docker image is published (considered and dropped — see
  DESIGN.md). The pattern above relies only on the nuget.org-published global tool (QP-11).

## Portability

- **POR-1** — The tool runs on Windows, Linux, and macOS.
- **POR-2** — The tool targets `net8.0` and is tested, via a CI SDK matrix, against both the .NET 8
  and .NET 10 SDKs.

## Quality / Process

- **QP-1** — The CLI is implemented with Argu.
- **QP-2** — Unit tests (`DotnetIsolate.UnitTests`) and integration tests
  (`DotnetIsolate.IntegrationTests`) are separate projects; both run in CI on every push (GitHub
  Actions).
- **QP-3** — Integration tests exercise the tool against a fixed fixture solution with five
  projects — `ServiceA`, `ServiceB`, `LogicA`, `LogicB`, `LogicCommon` — where `ServiceA` depends on
  `LogicA` + `LogicCommon` and `ServiceB` depends on `LogicB`, which itself depends on
  `LogicCommon`. Because this input space is small and fully known, the integration suite is
  expected to cover the tool's complete logic. Two fixtures with this shape exist
  (`src/TestSolutions/DiamondWithIncludedFiles*/`): a `.slnx`/net10.0 one and a `.sln`/net8.0 one,
  so both solution formats are covered without gating the whole CI matrix on a newer SDK.
- **QP-4** — Integration test coverage must be ≥ 90%, measured against the *entire*
  `DotnetIsolate.Core` assembly (`DotnetIsolate.IntegrationTests/coverage.integration.runsettings`).
  This is what gates publishing an alpha package to nuget.org on a green commit to the default
  branch.
- **QP-5** — Unit test coverage must be ≥ 95%, enforced as a build-breaking CI check — consistent
  with a TDD workflow where nearly all logic is covered by unit tests before the integration suite
  exercises it end-to-end. Measured with IO-touching code excluded
  (`DotnetIsolate.UnitTests/coverage.unit.runsettings`) — see DESIGN.md for why measuring the
  whole assembly here would make 95% structurally unreachable, and why that exclusion is a
  per-invocation coverlet filter rather than `[<ExcludeFromCodeCoverage>]` (which would also
  blind QP-4 to the same code, verified directly).
- **QP-6** — Unit and integration coverage are tracked and reported separately, not merged into a
  single number.
- **QP-7** — Every commit merged to the default branch that passes CI (including QP-4/QP-5) is
  published as a prerelease package (versioned via Nerdbank.GitVersioning: `X.Y.<git height>`
  with a `-g<shorthash>` suffix — a genuine SemVer/NuGet prerelease, hidden from default search,
  same effect as a literal `-alpha` label without needing a manual edit to remove one at release
  time). Only git tags matching `v<major>.<minor>` produce a clean, non-prerelease stable release,
  following semantic versioning.
- **QP-8** — Commits are GPG-signed. (One-time environment/setup task, not a design concern.)
- **QP-9** — A GitHub Actions pipeline runs a platform × .NET-version matrix for tests.
- **QP-10** — Code stays clean: small, easily readable functions, sensibly split
  namespaces/modules.
- **QP-11** — The tool is published to nuget.org as a .NET global tool.
- **QP-12** — A separate end-to-end test suite drives real `docker build` runs — twice, back to
  back, with a file changed in between — and asserts the expected layers are cache-hit on the second
  run. Two cases, since they prove different things:
  1. A file changed **outside** the isolated closure leaves the whole output unchanged, so
     restore/build/publish all stay cached even though the isolate step demonstrably reran. This
     covers the `COPY --from` bridge itself.
  2. A file changed **inside** the isolated closure must still leave `RUN dotnet restore` `CACHED`
     while the build layer reruns — the property the whole `--restore` split (FR-11, DI-1) rests on,
     and one the first case says nothing about.
  3. A changed **build artifact** (FR-15) reruns the bind-mounted isolate step — BuildKit keys a
     `RUN --mount=type=bind` off the whole mounted subtree, not the process's read-set — while
     restore/build/publish stay cached, because the artifact never reaches the isolated output.
  All three are asserted against real buildx builds. The classic builder is no longer exercised, since
  DI-1 no longer claims to support it. The suite requires a Docker daemon, so it is not part of the
  coverage gates in QP-4/QP-5.

## Out of scope for v1

- NuGet package references are not specially resolved or vendored — restore is expected to run
  normally against the ambient NuGet feeds inside the isolated folder.
- Per-`TargetFramework` file filtering for multi-targeted projects — a project with
  `<TargetFrameworks>net8.0;net10.0</TargetFrameworks>` gets all frameworks' files included, not
  just the one that will actually be built downstream.
