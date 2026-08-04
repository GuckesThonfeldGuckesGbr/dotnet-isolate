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
  source solution, e.g. `.slnx`), derived from the source solution as a template but containing
  only the included projects. This lets `dotnet build`/`dotnet restore` run at the output root with
  no arguments.
- **FR-4** — The tool includes every file an included project's `Compile`, `Content`, `None`, and
  `EmbeddedResource` MSBuild items resolve to (via real MSBuild evaluation — see DESIGN.md), plus
  `ProjectReference` targets, recursively.
- **FR-5** — The tool walks up from each included project's directory and includes any
  `Directory.Build.props`, `Directory.Build.targets`, `Directory.Packages.props`, `NuGet.config`,
  and `global.json` files it finds along the way, since MSBuild implicitly consumes these during
  restore/build even though they're never referenced explicitly by a project.
- **FR-6** — The output location defaults to `./<ProjectName>` and is overridable with
  `-o`/`--output-dir`, which accepts any relative or absolute path (not just a plain name created
  inside the current directory).
- **FR-7** — If the output folder already exists, the tool deletes it and recreates it from scratch
  before writing, so stale files from a previous run never linger.

## Performance

- **PR-1** — The analysis phase (determining the full set of files/projects to include) completes
  in under 1 second, even for a complex solution.

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
  `LogicA` + `LogicCommon` and `ServiceB` depends on `LogicB` + `LogicCommon`. Because this input
  space is small and fully known, the integration suite is expected to cover the tool's complete
  logic.
- **QP-4** — Integration test coverage must be ≥ 90%. This is what gates publishing an alpha
  package to nuget.org on a green commit to the default branch.
- **QP-5** — Unit test coverage must be ≥ 95%, enforced as a build-breaking CI check — consistent
  with a TDD workflow where nearly all logic is covered by unit tests before the integration suite
  exercises it end-to-end.
- **QP-6** — Unit and integration coverage are tracked and reported separately, not merged into a
  single number.
- **QP-7** — Every commit merged to the default branch that passes CI (including QP-4/QP-5) is
  published as an alpha/prerelease package. Only git tags produce a non-alpha (stable) release,
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
