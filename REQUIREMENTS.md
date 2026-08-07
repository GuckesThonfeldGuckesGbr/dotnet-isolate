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
- **FR-6** — The output location defaults to `./<ProjectName>` and is overridable with
  `-o`/`--output-dir`, which accepts any relative or absolute path (not just a plain name created
  inside the current directory).
- **FR-7** — If the output folder already exists, the tool deletes it and recreates it from scratch
  before writing, so stale files from a previous run never linger.
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

- **DI-1** — Two supported Dockerfile patterns are documented, both of which work under any
  Docker builder (classic or BuildKit) because they bridge the uncacheable "copy the whole solution
  in" step into the expensive downstream steps via a `COPY --from=<stage>`, which Docker always
  caches by checksumming the copied bytes rather than by chaining off the source stage's own layer
  history — see DESIGN.md ("Docker integration patterns") for the mechanism:
  1. **Self-contained, two-stage** — `dotnet isolate` runs in its own build stage (tool installed
     via `dotnet tool install -g` there). No host/CI-runner setup beyond Docker itself.
  2. **Host-side** — `dotnet isolate` runs on the host/CI runner *before* `docker build`, and the
     Dockerfile just `COPY`s the resulting (deterministic, per REL-1) output folder from the build
     context. No .NET SDK needed in any build stage.
- **DI-2** — Neither pattern requires modifying the original solution/repo.
- **DI-3** — No dedicated `dotnet-isolate` Docker image is published (considered and dropped — see
  DESIGN.md). Both patterns above rely only on the nuget.org-published global tool (QP-11).

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
  back, with an unrelated file changed in between — against both Dockerfile patterns in DI-1, and
  asserts the expected layers are cache-hit on the second run. Deliberately exercises the classic
  builder (not just BuildKit), since that's the less obvious case the DI-1 mechanism depends on. It
  requires a Docker daemon, so it is not part of the coverage gates in QP-4/QP-5.

## Out of scope for v1

- NuGet package references are not specially resolved or vendored — restore is expected to run
  normally against the ambient NuGet feeds inside the isolated folder.
- Per-`TargetFramework` file filtering for multi-targeted projects — a project with
  `<TargetFrameworks>net8.0;net10.0</TargetFrameworks>` gets all frameworks' files included, not
  just the one that will actually be built downstream.
