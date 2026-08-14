# Build-Artifact Exclusion (FR-15) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Stop generated build artifacts (`bin/`, `obj/`, `node_modules/`, artifacts-layout output, build logs) reaching the isolated output, and collapse the per-file stale-entry warning into one summary line.

**Architecture:** A new pure module `BuildArtifacts.fs` decides whether a resolved path is an artifact, given a `ContainsProjectFile` predicate (rules A–C) and a list of MSBuild-declared output directories (rule D). `BuildArtifactsIo.fs` supplies the memoized real-filesystem predicate. `Pipeline.isolate` collects rule D's directories from the per-project MSBuild evaluation it already performs, applies the filter between file resolution and `OutputSafety.partitionInputs`, and reports the excluded set. A new pure `Report.fs` formats every console warning line so the CLI's output is unit-testable.

**Tech Stack:** F# 8, .NET 8 target, xUnit, coverlet, Argu.

**Spec:** `docs/superpowers/specs/2026-08-14-build-artifact-exclusion-design.md`

## Global Constraints

- **POR-1** — must work on Windows, Linux and macOS. Never hardcode `"/repo/src/A"` in a test; use `DotnetIsolate.UnitTests.PathHelpers.path [ "repo"; "src"; "A" ]`. Never assert on path *strings*; compare against a path built the same way, or assert on `Path.GetFileName`.
- **QP-5** — unit coverage ≥ 95%, measured with IO code excluded. Every new `*Io.fs` module MUST be added to the `<Exclude>` list in `DotnetIsolate.UnitTests/coverage.unit.runsettings`, by class name, in the form `[DotnetIsolate.Core]DotnetIsolate.Core.<Module>`.
- **QP-4** — integration coverage ≥ 90% against the whole `DotnetIsolate.Core` assembly (no exclusions).
- **QP-10** — small, readable functions; pure logic and IO in separate files.
- F# compile order is significant. New files go in `DotnetIsolate.Core.fsproj` in dependency order: `BuildArtifacts.fs` and `BuildArtifactsIo.fs` **after** `MirrorRoot.fs` (rule D uses `MirrorRoot.isUnder`) and **before** `OutputSafety.fs`; `Report.fs` **after** `Pipeline.fs` (it formats `Pipeline.IsolateResult`).
- Repository files are LF (`.gitattributes`). Do not introduce CRLF.
- Commit messages end with `Co-Authored-By: Claude Opus 5 <noreply@anthropic.com>`. Commit to `main` (trunk-based); commit only after build and tests pass.

**Commands used throughout** (run from `src/DotnetIsolate`):

```bash
dotnet build DotnetIsolate.sln
dotnet test DotnetIsolate.UnitTests/DotnetIsolate.UnitTests.fsproj
dotnet test DotnetIsolate.IntegrationTests/DotnetIsolate.IntegrationTests.fsproj
```

---

### Task 1: `BuildArtifacts` pure rules

**Files:**
- Create: `src/DotnetIsolate/DotnetIsolate.Core/BuildArtifacts.fs`
- Modify: `src/DotnetIsolate/DotnetIsolate.Core/DotnetIsolate.Core.fsproj` (add after `MirrorRoot.fs`)
- Create: `src/DotnetIsolate/DotnetIsolate.UnitTests/BuildArtifactsTests.fs`
- Modify: `src/DotnetIsolate/DotnetIsolate.UnitTests/DotnetIsolate.UnitTests.fsproj` (add before `Program.fs`)

**Interfaces:**
- Consumes: `MirrorRoot.isUnder : string -> string -> bool` (already exists).
- Produces: `BuildArtifacts.ContainsProjectFile = string -> bool`; `BuildArtifacts.isBuildArtifact : ContainsProjectFile -> string list -> string -> bool`; `BuildArtifacts.Partition = { Kept: string list; Excluded: string list }`; `BuildArtifacts.partition : ContainsProjectFile -> string list -> string list -> Partition`.

- [ ] **Step 1: Write the failing tests**

```fsharp
module DotnetIsolate.UnitTests.BuildArtifactsTests

open Xunit
open DotnetIsolate.Core
open DotnetIsolate.UnitTests.PathHelpers

/// A ContainsProjectFile that says yes for exactly the directories given.
let private projectsIn (dirs: string list) : BuildArtifacts.ContainsProjectFile =
    let set = Set.ofList dirs
    fun dir -> Set.contains dir set

[<Fact>]
let ``rule A excludes obj content when a project file sits beside it`` () =
    let app = path [ "repo"; "App" ]
    let file = path [ "repo"; "App"; "obj"; "project.assets.json" ]

    Assert.True(BuildArtifacts.isBuildArtifact (projectsIn [ app ]) [] file)

[<Fact>]
let ``rule A keeps a bin directory with no project file beside it`` () =
    let file = path [ "repo"; "tools"; "bin"; "build.sh" ]

    Assert.False(BuildArtifacts.isBuildArtifact (projectsIn []) [] file)

[<Fact>]
let ``rule A excludes deeply nested build output`` () =
    let app = path [ "repo"; "App" ]
    let file = path [ "repo"; "App"; "obj"; "Debug"; "net8.0"; "App.AssemblyInfo.cs" ]

    Assert.True(BuildArtifacts.isBuildArtifact (projectsIn [ app ]) [] file)

[<Fact>]
let ``rule A excludes TestResults beside a project file`` () =
    let tests = path [ "repo"; "Tests" ]
    let file = path [ "repo"; "Tests"; "TestResults"; "run.trx" ]

    Assert.True(BuildArtifacts.isBuildArtifact (projectsIn [ tests ]) [] file)

[<Fact>]
let ``rule A is case-insensitive`` () =
    let app = path [ "repo"; "App" ]
    let file = path [ "repo"; "App"; "OBJ"; "project.assets.json" ]

    Assert.True(BuildArtifacts.isBuildArtifact (projectsIn [ app ]) [] file)

[<Fact>]
let ``rule A does not fire on a prefix-sharing sibling directory`` () =
    let app = path [ "repo"; "App" ]
    let file = path [ "repo"; "App"; "obj2"; "notes.txt" ]

    Assert.False(BuildArtifacts.isBuildArtifact (projectsIn [ app ]) [] file)

[<Fact>]
let ``rule B excludes node_modules with no anchor`` () =
    let file = path [ "repo"; "Web"; "node_modules"; "pkg"; "index.js" ]

    Assert.True(BuildArtifacts.isBuildArtifact (projectsIn []) [] file)

[<Fact>]
let ``rule C excludes build logs and coverage reports`` () =
    let predicate = projectsIn []

    Assert.True(BuildArtifacts.isBuildArtifact predicate [] (path [ "repo"; "msbuild.binlog" ]))
    Assert.True(BuildArtifacts.isBuildArtifact predicate [] (path [ "repo"; "coverage.cobertura.xml" ]))
    Assert.True(BuildArtifacts.isBuildArtifact predicate [] (path [ "repo"; "run.coverage" ]))

[<Fact>]
let ``rule C does not fire on a file merely containing a pattern`` () =
    let predicate = projectsIn []

    Assert.False(BuildArtifacts.isBuildArtifact predicate [] (path [ "repo"; "my.binlog.txt" ]))
    Assert.False(BuildArtifacts.isBuildArtifact predicate [] (path [ "repo"; "notes.md" ]))

[<Fact>]
let ``rule D excludes files under a declared output directory`` () =
    let artifacts = path [ "repo"; "artifacts" ]
    let file = path [ "repo"; "artifacts"; "obj"; "App"; "project.assets.json" ]

    Assert.True(BuildArtifacts.isBuildArtifact (projectsIn []) [ artifacts ] file)

[<Fact>]
let ``rule D compares segment-wise, not by string prefix`` () =
    let artifacts = path [ "repo"; "artifacts" ]
    let file = path [ "repo"; "artifacts2"; "src"; "Program.cs" ]

    Assert.False(BuildArtifacts.isBuildArtifact (projectsIn []) [ artifacts ] file)

[<Fact>]
let ``an empty declared-directory list excludes nothing by rule D`` () =
    let file = path [ "repo"; "src"; "Program.cs" ]

    Assert.False(BuildArtifacts.isBuildArtifact (projectsIn []) [] file)

[<Fact>]
let ``partition splits kept from excluded and preserves order`` () =
    let app = path [ "repo"; "App" ]
    let source = path [ "repo"; "App"; "Program.cs" ]
    let artifact = path [ "repo"; "App"; "obj"; "project.assets.json" ]
    let other = path [ "repo"; "App"; "appsettings.json" ]

    let result = BuildArtifacts.partition (projectsIn [ app ]) [] [ source; artifact; other ]

    Assert.Equal<string list>([ source; other ], result.Kept)
    Assert.Equal<string list>([ artifact ], result.Excluded)
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test DotnetIsolate.UnitTests/DotnetIsolate.UnitTests.fsproj --filter BuildArtifactsTests`
Expected: compile error — `BuildArtifacts` is not defined.

- [ ] **Step 3: Write the implementation**

```fsharp
module DotnetIsolate.Core.BuildArtifacts

open System
open System.IO

/// Directory names that hold generated build output *when a project file sits beside them*
/// (FR-15 rule A). The anchor is what makes the rule safe: `Web/bin` next to `Web.csproj` is
/// build output, while a checked-in `tools/bin/build.sh` is not. `TestResults` belongs here
/// rather than in the unanchored list for the same reason - a test run creates it next to the
/// test project, but the name is plausible enough for a fixture directory to warrant the anchor.
let projectAnchoredDirectoryNames = [ "bin"; "obj"; "TestResults" ]

/// Directory names that are package-manager restore output wherever they appear (FR-15 rule B).
/// Same reasoning DESIGN.md gives for excluding the `Analyzer` and `Reference` item types: the
/// contents are restored inside the container from a manifest, so shipping them is pure cost -
/// and `node_modules` is usually the largest single source of cache churn in a repo that has one.
let unanchoredDirectoryNames = [ "node_modules" ]

/// Whether a *file name* is a generated build log or coverage report (FR-15 rule C). Suffix
/// matching, not substring: `my.binlog.txt` is somebody's notes, not a binary log.
let isGeneratedReportName (name: string) =
    let endsWith (suffix: string) =
        name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)

    endsWith ".binlog"
    || endsWith ".coverage"
    || endsWith ".cobertura.xml"
    || (name.StartsWith("coverage.", StringComparison.OrdinalIgnoreCase) && endsWith ".xml")

/// Whether a directory directly contains a project file. Injected so the rules stay pure and
/// testable; BuildArtifactsIo supplies the real, memoized filesystem implementation.
type ContainsProjectFile = string -> bool

/// Every ancestor directory of `file`, nearest first, ending at the filesystem root.
let private ancestorDirectories (file: string) : string list =
    let rec go (dir: string option) =
        match dir with
        | None -> []
        | Some d -> d :: go (Path.GetDirectoryName(d) |> Option.ofObj)

    go (Path.GetDirectoryName(file) |> Option.ofObj)

let private equalsIgnoreCase (a: string) (b: string) =
    String.Equals(a, b, StringComparison.OrdinalIgnoreCase)

/// Whether `file` is a generated build artifact rather than a build input (FR-15).
///
/// `declaredOutputDirectories` is rule D: absolute directories MSBuild itself named as output
/// (`ArtifactsPath`, `BaseOutputPath`, `BaseIntermediateOutputPath`). It exists because the
/// `UseArtifactsOutput` layout relocates everything to a repo-root `artifacts/` tree whose
/// `bin`/`obj` directories have no project file beside them, so rule A cannot see them - and
/// because those paths sit *above* the projects, letting them through would raise the mirror
/// root (FR-2) and shift every path in the output, not just leak files.
let isBuildArtifact
    (containsProjectFile: ContainsProjectFile)
    (declaredOutputDirectories: string list)
    (file: string)
    : bool =
    let underDeclaredOutput =
        declaredOutputDirectories |> List.exists (fun dir -> MirrorRoot.isUnder dir file)

    let isGeneratedReport = isGeneratedReportName (Path.GetFileName(file))

    let underArtifactDirectory () =
        ancestorDirectories file
        |> List.exists (fun dir ->
            let name = Path.GetFileName(dir)

            if unanchoredDirectoryNames |> List.exists (equalsIgnoreCase name) then
                true
            elif projectAnchoredDirectoryNames |> List.exists (equalsIgnoreCase name) then
                match Path.GetDirectoryName(dir) |> Option.ofObj with
                | Some parent -> containsProjectFile parent
                | None -> false
            else
                false)

    underDeclaredOutput || isGeneratedReport || underArtifactDirectory ()

/// A file list split by `isBuildArtifact`.
type Partition = { Kept: string list; Excluded: string list }

/// Splits `files` into genuine build inputs and generated artifacts, preserving input order.
let partition
    (containsProjectFile: ContainsProjectFile)
    (declaredOutputDirectories: string list)
    (files: string list)
    : Partition =
    let excluded, kept =
        files |> List.partition (isBuildArtifact containsProjectFile declaredOutputDirectories)

    { Kept = kept; Excluded = excluded }
```

Add to `DotnetIsolate.Core.fsproj` immediately after `<Compile Include="MirrorRoot.fs"/>`:

```xml
        <Compile Include="BuildArtifacts.fs"/>
```

Add to `DotnetIsolate.UnitTests.fsproj` immediately before `<Compile Include="Program.fs"/>`:

```xml
        <Compile Include="BuildArtifactsTests.fs"/>
```

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test DotnetIsolate.UnitTests/DotnetIsolate.UnitTests.fsproj --filter BuildArtifactsTests`
Expected: PASS, 13 tests.

- [ ] **Step 5: Commit**

```bash
git add src/DotnetIsolate/DotnetIsolate.Core/BuildArtifacts.fs \
        src/DotnetIsolate/DotnetIsolate.Core/DotnetIsolate.Core.fsproj \
        src/DotnetIsolate/DotnetIsolate.UnitTests/BuildArtifactsTests.fs \
        src/DotnetIsolate/DotnetIsolate.UnitTests/DotnetIsolate.UnitTests.fsproj
git commit -m "feat: add the build-artifact classification rules (FR-15)"
```

---

### Task 2: `BuildArtifactsIo` filesystem predicate

**Files:**
- Create: `src/DotnetIsolate/DotnetIsolate.Core/BuildArtifactsIo.fs`
- Modify: `src/DotnetIsolate/DotnetIsolate.Core/DotnetIsolate.Core.fsproj` (after `BuildArtifacts.fs`)
- Modify: `src/DotnetIsolate/DotnetIsolate.UnitTests/coverage.unit.runsettings` (QP-5 exclusion)
- Create: `src/DotnetIsolate/DotnetIsolate.IntegrationTests/BuildArtifactsTests.fs`
- Modify: `src/DotnetIsolate/DotnetIsolate.IntegrationTests/DotnetIsolate.IntegrationTests.fsproj` (before `Program.fs`)

**Interfaces:**
- Consumes: `BuildArtifacts.ContainsProjectFile` from Task 1.
- Produces: `BuildArtifactsIo.containsProjectFile : BuildArtifacts.ContainsProjectFile`.

- [ ] **Step 1: Write the failing integration test**

```fsharp
module DotnetIsolate.IntegrationTests.BuildArtifactsTests

open System.IO
open Xunit
open DotnetIsolate.Core
open DotnetIsolate.IntegrationTests.TestFixtures

[<Fact>]
let ``containsProjectFile sees a real project file and nothing else`` () =
    withTempDir (fun root ->
        let projectDir = Path.Combine(root, "App")
        let plainDir = Path.Combine(root, "tools")
        Directory.CreateDirectory(projectDir) |> ignore
        Directory.CreateDirectory(plainDir) |> ignore
        File.WriteAllText(Path.Combine(projectDir, "App.csproj"), "<Project/>")
        File.WriteAllText(Path.Combine(plainDir, "build.sh"), "echo hi")

        Assert.True(BuildArtifactsIo.containsProjectFile projectDir)
        Assert.False(BuildArtifactsIo.containsProjectFile plainDir)
        Assert.False(BuildArtifactsIo.containsProjectFile (Path.Combine(root, "does-not-exist"))))

[<Fact>]
let ``containsProjectFile recognises fsproj and vbproj too`` () =
    withTempDir (fun root ->
        let fsharpDir = Path.Combine(root, "F")
        let vbDir = Path.Combine(root, "V")
        Directory.CreateDirectory(fsharpDir) |> ignore
        Directory.CreateDirectory(vbDir) |> ignore
        File.WriteAllText(Path.Combine(fsharpDir, "F.fsproj"), "<Project/>")
        File.WriteAllText(Path.Combine(vbDir, "V.vbproj"), "<Project/>")

        Assert.True(BuildArtifactsIo.containsProjectFile fsharpDir)
        Assert.True(BuildArtifactsIo.containsProjectFile vbDir))
```

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet test DotnetIsolate.IntegrationTests/DotnetIsolate.IntegrationTests.fsproj --filter BuildArtifactsTests`
Expected: compile error — `BuildArtifactsIo` is not defined.

- [ ] **Step 3: Write the implementation**

```fsharp
/// Real filesystem implementation of BuildArtifacts.ContainsProjectFile, kept in its own file so
/// coverlet's whole-class exclude filter can scope it out of the unit coverage gate (QP-5)
/// without affecting the integration gate (QP-4) - see FileResolutionIo.fs for the full rationale.
module DotnetIsolate.Core.BuildArtifactsIo

open System.Collections.Concurrent
open System.IO

let private projectFilePatterns = [ "*.csproj"; "*.fsproj"; "*.vbproj" ]

/// A `BuildArtifacts.ContainsProjectFile` backed by the real filesystem.
///
/// Memoized: a large solution asks about the same `obj/` parent once per resolved file under it,
/// which is hundreds of identical directory enumerations otherwise. A ConcurrentDictionary
/// because the pipeline evaluates projects in parallel (see Pipeline.fs), and because this is a
/// single shared value rather than one instance per run - the cache is keyed by absolute
/// directory path, so entries from one run are still correct for the next within a process.
let containsProjectFile: BuildArtifacts.ContainsProjectFile =
    let cache = ConcurrentDictionary<string, bool>()

    fun directory ->
        cache.GetOrAdd(
            directory,
            fun dir ->
                Directory.Exists(dir)
                && projectFilePatterns
                   |> List.exists (fun pattern -> Directory.EnumerateFiles(dir, pattern) |> Seq.isEmpty |> not)
        )
```

Add to `DotnetIsolate.Core.fsproj` immediately after `<Compile Include="BuildArtifacts.fs"/>`:

```xml
        <Compile Include="BuildArtifactsIo.fs"/>
```

Add to `DotnetIsolate.IntegrationTests.fsproj` immediately before `<Compile Include="Program.fs"/>`:

```xml
        <Compile Include="BuildArtifactsTests.fs"/>
```

In `coverage.unit.runsettings`, add `,[DotnetIsolate.Core]DotnetIsolate.Core.BuildArtifactsIo` to the `<Exclude>` list (QP-5).

- [ ] **Step 4: Run to verify it passes**

Run: `dotnet test DotnetIsolate.IntegrationTests/DotnetIsolate.IntegrationTests.fsproj --filter BuildArtifactsTests`
Expected: PASS, 2 tests.

- [ ] **Step 5: Commit**

```bash
git add src/DotnetIsolate/DotnetIsolate.Core/BuildArtifactsIo.fs \
        src/DotnetIsolate/DotnetIsolate.Core/DotnetIsolate.Core.fsproj \
        src/DotnetIsolate/DotnetIsolate.UnitTests/coverage.unit.runsettings \
        src/DotnetIsolate/DotnetIsolate.IntegrationTests/BuildArtifactsTests.fs \
        src/DotnetIsolate/DotnetIsolate.IntegrationTests/DotnetIsolate.IntegrationTests.fsproj
git commit -m "feat: back the project-file anchor with a memoized filesystem probe"
```

---

### Task 3: Rule D's declared output directories from MSBuild

**Files:**
- Modify: `src/DotnetIsolate/DotnetIsolate.Core/FileResolution.fs` (add after `filePathPropertyNames`)
- Modify: `src/DotnetIsolate/DotnetIsolate.Core/FileResolutionIo.fs` (re-export)
- Modify: `src/DotnetIsolate/DotnetIsolate.UnitTests/FileResolutionTests.fs` (append tests)

**Interfaces:**
- Produces: `FileResolution.outputDirectoryPropertyNames : string list`; `FileResolution.resolveOutputDirectories : Map<string,string> -> string -> string list`; `FileResolutionIo.outputDirectoryPropertyNames`.

- [ ] **Step 1: Write the failing tests** (append to `FileResolutionTests.fs`)

```fsharp
[<Fact>]
let ``resolveOutputDirectories makes relative output paths absolute against the project`` () =
    let projectPath = path [ "repo"; "App"; "App.fsproj" ]
    let properties = Map.ofList [ "BaseOutputPath", "bin/"; "BaseIntermediateOutputPath", "obj/" ]

    let result = FileResolution.resolveOutputDirectories properties projectPath

    Assert.Equal<string list>([ path [ "repo"; "App"; "bin" ]; path [ "repo"; "App"; "obj" ] ], result)

[<Fact>]
let ``resolveOutputDirectories keeps an absolute artifacts path and drops unset properties`` () =
    let projectPath = path [ "repo"; "App"; "App.fsproj" ]
    let artifacts = path [ "repo"; "artifacts" ]

    let properties =
        Map.ofList [ "ArtifactsPath", artifacts; "BaseOutputPath", ""; "BaseIntermediateOutputPath", "   " ]

    let result = FileResolution.resolveOutputDirectories properties projectPath

    Assert.Equal<string list>([ artifacts ], result)
```

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet test DotnetIsolate.UnitTests/DotnetIsolate.UnitTests.fsproj --filter FileResolutionTests`
Expected: compile error — `resolveOutputDirectories` is not defined.

- [ ] **Step 3: Write the implementation** (in `FileResolution.fs`, after `filePathPropertyNames`)

```fsharp
/// MSBuild *properties* naming a *directory* that holds generated build output (FR-15 rule D).
/// Distinct from `filePathPropertyNames`, whose values are input files and are existence-checked:
/// these are directories, they need not exist yet, and everything under them is excluded.
///
/// `ArtifactsPath` is the one that matters most and the reason this list exists at all. Under
/// `UseArtifactsOutput` every project's output moves to a repo-root `artifacts/` tree whose
/// `bin`/`obj` directories have no project file beside them, so the path-anchored rules cannot
/// see it. It is also *repo-wide* - one value shared by every project - so reading it from the
/// projects in the closure yields the correct directory for projects outside it too, which is
/// exactly the case a per-project property cannot cover.
let outputDirectoryPropertyNames =
    [ "ArtifactsPath"; "BaseOutputPath"; "BaseIntermediateOutputPath" ]

/// Resolves `outputDirectoryPropertyNames` to absolute directories for one project. Values may be
/// relative (`bin\`) or already absolute (`ArtifactsPath` evaluates absolute), so each is made
/// absolute against the project's own directory, exactly as `resolvePropertyFiles` does.
///
/// The trailing separator MSBuild leaves on `BaseOutputPath` is trimmed so the value is stored in
/// one canonical spelling, matching how Pipeline normalises the output directory.
///
/// `projectPath` must be *fully qualified* - not merely rooted; see `resolvePropertyFiles`.
let resolveOutputDirectories (properties: Map<string, string>) (projectPath: string) : string list =
    let projectDir = Path.GetDirectoryName(projectPath: string)

    outputDirectoryPropertyNames
    |> List.choose (fun name -> properties |> Map.tryFind name)
    |> List.map (fun value -> value.Trim())
    |> List.filter (fun value -> value <> "")
    |> List.map (fun value -> Path.TrimEndingDirectorySeparator(Path.GetFullPath(value, projectDir)))
    |> List.distinct
```

In `FileResolutionIo.fs`, after `filePathPropertyNames`:

```fsharp
/// The MSBuild properties naming directories of generated build output (FR-15 rule D).
let outputDirectoryPropertyNames = FileResolution.outputDirectoryPropertyNames
```

- [ ] **Step 4: Run to verify it passes**

Run: `dotnet test DotnetIsolate.UnitTests/DotnetIsolate.UnitTests.fsproj --filter FileResolutionTests`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add src/DotnetIsolate/DotnetIsolate.Core/FileResolution.fs \
        src/DotnetIsolate/DotnetIsolate.Core/FileResolutionIo.fs \
        src/DotnetIsolate/DotnetIsolate.UnitTests/FileResolutionTests.fs
git commit -m "feat: resolve MSBuild-declared output directories for rule D"
```

---

### Task 4: Wire the filter into the pipeline

**Files:**
- Modify: `src/DotnetIsolate/DotnetIsolate.Core/Pipeline.fs`
- Create: `src/DotnetIsolate/DotnetIsolate.IntegrationTests/BuildArtifactExclusionTests.fs`
- Modify: `src/DotnetIsolate/DotnetIsolate.IntegrationTests/DotnetIsolate.IntegrationTests.fsproj`

**Interfaces:**
- Consumes: `BuildArtifacts.partition`, `BuildArtifactsIo.containsProjectFile`, `FileResolution.resolveOutputDirectories`, `FileResolutionIo.outputDirectoryPropertyNames`.
- Produces: `Pipeline.IsolateResult.ExcludedArtifacts : string list`.

- [ ] **Step 1: Write the failing integration tests**

```fsharp
module DotnetIsolate.IntegrationTests.BuildArtifactExclusionTests

open System.IO
open Xunit
open DotnetIsolate.Core
open DotnetIsolate.IntegrationTests.TestFixtures

/// A project whose directory contains other projects globs their bin/obj in through the SDK's
/// default `**/*` None glob - DefaultItemExcludes only ever excludes a project's *own* output.
/// This is the layout that produced the original report.
[<Fact>]
let ``isolate excludes bin and obj artifacts of nested projects`` () =
    withTempDir (fun root ->
        writeProject (Path.Combine(root, "A")) "A" [ "B" ] []
        writeProject (Path.Combine(root, "B")) "B" [] []

        // Real artifacts, as a prior host-side build would leave them.
        let objDir = Path.Combine(root, "B", "obj")
        Directory.CreateDirectory(objDir) |> ignore
        File.WriteAllText(Path.Combine(objDir, "project.assets.json"), "{}")
        let binDir = Path.Combine(root, "B", "bin", "Debug", "net8.0")
        Directory.CreateDirectory(binDir) |> ignore
        File.WriteAllText(Path.Combine(binDir, "B.dll"), "MZ")

        let outputDir = Path.Combine(root, "output")

        let result =
            Pipeline.isolate
                { ProjectPaths = [ Path.Combine(root, "A", "A.fsproj") ]
                  OutputDir = Some outputDir
                  SolutionPath = None
                  RestoreOnly = false
                  Clean = false }

        Assert.True(File.Exists(Path.Combine(outputDir, "B", "B.fsproj")))
        Assert.False(Directory.Exists(Path.Combine(outputDir, "B", "obj")))
        Assert.False(Directory.Exists(Path.Combine(outputDir, "B", "bin")))
        Assert.DoesNotContain(result.ExcludedArtifacts, fun (f: string) -> f.EndsWith("B.fsproj")))

/// A hand-written glob bypasses DefaultItemExcludes entirely, so even a leaf project can pull its
/// own obj/ in. The filter works on resolved paths, so it covers this mechanism identically.
[<Fact>]
let ``isolate excludes obj content pulled in by a hand-written glob`` () =
    withTempDir (fun root ->
        let projectDir = Path.Combine(root, "A")
        Directory.CreateDirectory(projectDir) |> ignore

        File.WriteAllText(
            Path.Combine(projectDir, "A.fsproj"),
            """<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net8.0</TargetFramework>
  </PropertyGroup>
  <ItemGroup>
    <Compile Include="Program.fs"/>
    <None Include="**/*.json"/>
  </ItemGroup>
</Project>
"""
        )

        File.WriteAllText(Path.Combine(projectDir, "Program.fs"), "module A.Program\n")
        File.WriteAllText(Path.Combine(projectDir, "appsettings.json"), "{}")
        Directory.CreateDirectory(Path.Combine(projectDir, "obj")) |> ignore
        File.WriteAllText(Path.Combine(projectDir, "obj", "project.assets.json"), "{}")

        let outputDir = Path.Combine(root, "output")

        Pipeline.isolate
            { ProjectPaths = [ Path.Combine(projectDir, "A.fsproj") ]
              OutputDir = Some outputDir
              SolutionPath = None
              RestoreOnly = false
              Clean = false }
        |> ignore

        Assert.True(File.Exists(Path.Combine(outputDir, "A", "appsettings.json")))
        Assert.False(Directory.Exists(Path.Combine(outputDir, "A", "obj"))))
```

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet test DotnetIsolate.IntegrationTests/DotnetIsolate.IntegrationTests.fsproj --filter BuildArtifactExclusionTests`
Expected: FAIL — `ExcludedArtifacts` is not a field of `IsolateResult`.

- [ ] **Step 3: Write the implementation**

In `Pipeline.fs`, add to `IsolateResult` after `ExcludedUnderOutput`:

```fsharp
      /// Resolved inputs dropped because they are generated build artifacts rather than build
      /// inputs (FR-15).
      ExcludedArtifacts: string list
```

Add a resolver for rule D's directories next to `getPropertiesCached`. Because `evaluateCached`
already fetches every property in one call, extend `allPropertyNames`:

```fsharp
    let allPropertyNames =
        FileResolutionIo.filePathPropertyNames @ FileResolutionIo.outputDirectoryPropertyNames
```

After `let allFiles = (projectFiles @ implicitFiles) |> List.distinct`, and *before* the
`List.isEmpty allFiles` check, insert:

```fsharp
    // FR-15: drop generated build artifacts before anything downstream can see them. Placed here,
    // ahead of the output-directory partition and the mirror root, for the same reason FR-14's
    // missing-file drop is: an artifact must not inflate the mirror root (FR-2) - the artifacts
    // layout puts output *above* the projects, where letting it through would shift every path in
    // the output - nor count toward OutputSafety.validate's "contains every resolved input" check.
    let declaredOutputDirectories =
        projects
        |> List.collect (fun p -> FileResolution.resolveOutputDirectories (getPropertiesCached p) p)
        |> List.distinct

    let artifactPartition =
        BuildArtifacts.partition BuildArtifactsIo.containsProjectFile declaredOutputDirectories allFiles

    let allFiles = artifactPartition.Kept
```

Add `ExcludedArtifacts = artifactPartition.Excluded` to the returned record.

- [ ] **Step 4: Run to verify it passes**

Run: `dotnet test DotnetIsolate.IntegrationTests/DotnetIsolate.IntegrationTests.fsproj`
Expected: PASS, including the pre-existing pipeline tests.

- [ ] **Step 5: Commit**

```bash
git add src/DotnetIsolate/DotnetIsolate.Core/Pipeline.fs \
        src/DotnetIsolate/DotnetIsolate.IntegrationTests/BuildArtifactExclusionTests.fs \
        src/DotnetIsolate/DotnetIsolate.IntegrationTests/DotnetIsolate.IntegrationTests.fsproj
git commit -m "feat: exclude generated build artifacts from the isolated output (FR-15)"
```

---

### Task 5: Artifacts-layout coverage (rule D end to end)

**Files:**
- Modify: `src/DotnetIsolate/DotnetIsolate.IntegrationTests/BuildArtifactExclusionTests.fs`

**Interfaces:** consumes Task 4's pipeline behaviour; produces no new API.

The assertion that matters is not "no artifacts leaked" but "the mirror root did not move": with
`artifacts/` at the repo root, letting those paths through raises the common ancestor and shifts
every output path, which is a total cache miss rather than a partial one.

- [ ] **Step 1: Write the failing test**

```fsharp
/// UseArtifactsOutput relocates all output to a repo-root artifacts/ tree, whose bin/obj have no
/// project file beside them - invisible to the path rules, and *above* the projects, so letting it
/// through would raise the mirror root and shift every path in the output.
[<Fact>]
let ``isolate excludes the artifacts layout without moving the mirror root`` () =
    withTempDir (fun root ->
        writeProject (Path.Combine(root, "A")) "A" [] []

        File.WriteAllText(
            Path.Combine(root, "Directory.Build.props"),
            "<Project><PropertyGroup><UseArtifactsOutput>true</UseArtifactsOutput></PropertyGroup></Project>"
        )

        let artifactsObj = Path.Combine(root, "artifacts", "obj", "A")
        Directory.CreateDirectory(artifactsObj) |> ignore
        File.WriteAllText(Path.Combine(artifactsObj, "project.assets.json"), "{}")

        let outputDir = Path.Combine(root, "output")

        Pipeline.isolate
            { ProjectPaths = [ Path.Combine(root, "A", "A.fsproj") ]
              OutputDir = Some outputDir
              SolutionPath = None
              RestoreOnly = false
              Clean = false }
        |> ignore

        Assert.False(Directory.Exists(Path.Combine(outputDir, "artifacts")))

        // The mirror root is unchanged: A's project file still lands at output/A/A.fsproj, not
        // pushed a level deeper by a common ancestor raised to include artifacts/.
        Assert.True(File.Exists(Path.Combine(outputDir, "A", "A.fsproj")))
        Assert.True(File.Exists(Path.Combine(outputDir, "Directory.Build.props"))))
```

- [ ] **Step 2: Run to verify the assertion is meaningful**

Run: `dotnet test DotnetIsolate.IntegrationTests/DotnetIsolate.IntegrationTests.fsproj --filter BuildArtifactExclusionTests`
Expected: PASS with Task 4's implementation in place. If it fails, rule D is not reaching the pipeline — check that `allPropertyNames` includes `outputDirectoryPropertyNames`.

- [ ] **Step 3: Commit**

```bash
git add src/DotnetIsolate/DotnetIsolate.IntegrationTests/BuildArtifactExclusionTests.fs
git commit -m "test: prove the artifacts layout is excluded without moving the mirror root"
```

---

### Task 6: Collapse the console warnings

**Files:**
- Create: `src/DotnetIsolate/DotnetIsolate.Core/Report.fs`
- Modify: `src/DotnetIsolate/DotnetIsolate.Core/DotnetIsolate.Core.fsproj` (after `Pipeline.fs`)
- Modify: `src/DotnetIsolate/DotnetIsolate/Program.fs:53-65`
- Create: `src/DotnetIsolate/DotnetIsolate.UnitTests/ReportTests.fs`
- Modify: `src/DotnetIsolate/DotnetIsolate.UnitTests/DotnetIsolate.UnitTests.fsproj`

**Rationale for a new module:** `Program.fs` has no test project today, so warning text asserted
nowhere. Formatting is pure, so moving it into Core makes both new summary lines unit-testable and
keeps `Program.fs` to argument parsing plus printing.

**Interfaces:**
- Consumes: `Pipeline.IsolateResult`.
- Produces: `Report.warnings : Pipeline.IsolateResult -> string list`.

- [ ] **Step 1: Write the failing tests**

```fsharp
module DotnetIsolate.UnitTests.ReportTests

open Xunit
open DotnetIsolate.Core
open DotnetIsolate.UnitTests.PathHelpers

let private result () : Pipeline.IsolateResult =
    { OutputDir = path [ "repo"; "out" ]
      IncludedProjects = []
      FileCount = 0
      SolutionRoot = None
      Strategy = LinkStrategy.Copy
      ExcludedUnderOutput = []
      ExcludedArtifacts = []
      StaleEntries = []
      MissingFiles = []
      EntriesOutsideDiscoveredSolution = [] }

[<Fact>]
let ``no warnings are produced for a clean run`` () =
    Assert.Empty(Report.warnings (result ()))

[<Fact>]
let ``excluded artifacts collapse into a single counted line`` () =
    let r =
        { result () with
            ExcludedArtifacts = [ path [ "a" ]; path [ "b" ]; path [ "c" ] ] }

    let line = Report.warnings r |> List.exactlyOne

    Assert.Contains("3", line)
    Assert.Contains("build artifact", line)

[<Fact>]
let ``stale entries collapse into a single counted line mentioning --clean`` () =
    let r =
        { result () with
            StaleEntries = [ path [ "a" ]; path [ "b" ] ] }

    let line = Report.warnings r |> List.exactlyOne

    Assert.Contains("not clean", line)
    Assert.Contains("2", line)
    Assert.Contains("--clean", line)

[<Fact>]
let ``missing files and files under the output are still reported per file`` () =
    let missing = path [ "repo"; "gone.txt" ]
    let underOutput = path [ "repo"; "out"; "stale.txt" ]

    let r =
        { result () with
            MissingFiles = [ missing ]
            ExcludedUnderOutput = [ underOutput ] }

    let warnings = Report.warnings r

    Assert.Equal(2, warnings.Length)
    Assert.Contains(warnings, fun w -> w.Contains(missing))
    Assert.Contains(warnings, fun w -> w.Contains(underOutput))
```

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet test DotnetIsolate.UnitTests/DotnetIsolate.UnitTests.fsproj --filter ReportTests`
Expected: compile error — `Report` is not defined.

- [ ] **Step 3: Write the implementation**

```fsharp
module DotnetIsolate.Core.Report

/// The warning lines a run should print to stderr, in order.
///
/// Two of the four categories are summarised rather than listed. Artifact exclusions (FR-15) and
/// stale output entries (FR-7) arrive in bulk - hundreds of lines carrying one bit of information
/// between them - while a missing item file (FR-14) and a resolved input sitting under the output
/// directory are each rare and individually actionable, so those stay per file.
let warnings (result: Pipeline.IsolateResult) : string list =
    [ if not (List.isEmpty result.ExcludedArtifacts) then
          $"note: skipped {result.ExcludedArtifacts.Length} generated build artifact(s) (bin/, obj/, node_modules/, build and coverage logs)"

      if not (List.isEmpty result.StaleEntries) then
          $"warning: the output directory was not clean ({result.StaleEntries.Length} pre-existing file(s) left in place); use --clean to replace it"

      for entry in result.ExcludedUnderOutput do
          $"warning: ignoring {entry}, which lives under the output directory"

      for entry in result.MissingFiles do
          $"warning: {entry} is referenced by a project but does not exist; skipped"

      // result.SolutionRoot is always Some here when this list is non-empty (see Pipeline.fs).
      for entry in result.EntriesOutsideDiscoveredSolution do
          $"warning: {entry} lies outside the discovered solution ({result.SolutionRoot.Value.SolutionFile}); the generated solution file may not include it" ]
```

Add to `DotnetIsolate.Core.fsproj` immediately after `<Compile Include="Pipeline.fs"/>`:

```xml
        <Compile Include="Report.fs"/>
```

Add `<Compile Include="ReportTests.fs"/>` to `DotnetIsolate.UnitTests.fsproj` before `Program.fs`.

Replace `Program.fs` lines 53-65 (the four `for entry in ...` loops) with:

```fsharp
        for warning in Report.warnings result do
            eprintfn $"{warning}"
```

- [ ] **Step 4: Run the full suite**

Run: `dotnet build DotnetIsolate.sln && dotnet test DotnetIsolate.UnitTests/DotnetIsolate.UnitTests.fsproj && dotnet test DotnetIsolate.IntegrationTests/DotnetIsolate.IntegrationTests.fsproj`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add src/DotnetIsolate/DotnetIsolate.Core/Report.fs \
        src/DotnetIsolate/DotnetIsolate.Core/DotnetIsolate.Core.fsproj \
        src/DotnetIsolate/DotnetIsolate/Program.fs \
        src/DotnetIsolate/DotnetIsolate.UnitTests/ReportTests.fs \
        src/DotnetIsolate/DotnetIsolate.UnitTests/DotnetIsolate.UnitTests.fsproj
git commit -m "feat: summarise artifact exclusions and stale output entries"
```

---

### Task 7: Documentation

**Files:**
- Modify: `REQUIREMENTS.md` (add FR-15 after FR-14; amend FR-7)
- Modify: `DESIGN.md` (file-resolution section)
- Modify: `README.md` (only if it enumerates what lands in the output)

- [ ] **Step 1: Add FR-15 to `REQUIREMENTS.md`, after FR-14**

```markdown
- **FR-15** — Generated build artifacts are excluded from the isolated output, since they are not
  build inputs and actively defeat the tool's purpose: `project.assets.json` and `*.nuget.g.props`
  embed absolute *host* paths that are wrong inside a container, `obj/**` caches can make the
  container's build believe it is already up to date, and every host-side rebuild rewrites them —
  changing the output's bytes, changing the `COPY --from` cache key (DI-1), and rerunning restore
  for no source change. Four rules, applied to resolved paths before the mirror root is computed
  (FR-2) so an artifact cannot inflate it: (a) a `bin`, `obj` or `TestResults` directory whose
  parent holds a project file — the anchor keeps a checked-in `tools/bin/build.sh` safe; (b) a
  `node_modules` directory anywhere, on the same reasoning that excludes the `Analyzer` and
  `Reference` item types, namely that it is restored inside the container from a manifest;
  (c) `*.binlog`, `*.coverage`, `*.cobertura.xml` and `coverage.*.xml`; and (d) anything under a
  directory MSBuild itself declared as output via `ArtifactsPath`, `BaseOutputPath` or
  `BaseIntermediateOutputPath`. Rules (a) and (d) are both needed because they are blind in
  opposite directions — (a) cannot see output relocated by `UseArtifactsOutput`, whose `bin`/`obj`
  sit under a repo-root `artifacts/` with no project file beside them, and (d) can only name the
  output directories of projects in the closure, while the artifacts that leak most often belong to
  projects outside it. A file genuinely checked in under a project's own `bin`/`obj` is dropped;
  this is accepted, and the count of exclusions is reported.
```

- [ ] **Step 2: Amend FR-7**

Change "every entry the run did not itself produce is reported to the user" to "the number of
entries the run did not itself produce is reported to the user".

- [ ] **Step 3: Document the three mechanisms in `DESIGN.md`**

In the file-resolution section, record why artifacts appear in the resolved set at all — the SDK
excludes only a project's *own* `bin`/`obj`, so a project containing other projects globs theirs in;
a hand-written `<None Include="**/*"/>` bypasses `DefaultItemExcludes` entirely; and
`UseArtifactsOutput` relocates output above the projects, where the real cost is mirror-root
inflation rather than the leaked files. Note the verified negative: the generated
`*.GeneratedMSBuildEditorConfig.editorconfig` is added by a target, not at evaluation, so
`-getItem:EditorConfigFiles` never returns it.

- [ ] **Step 4: Verify no stale claims remain**

Run: `grep -rn "was already in the output directory" README.md DESIGN.md REQUIREMENTS.md`
Expected: no matches (the old per-file wording is gone).

- [ ] **Step 5: Commit**

```bash
git add REQUIREMENTS.md DESIGN.md README.md
git commit -m "docs: specify FR-15 and the collapsed output-directory reporting"
```

---

## Self-Review

**Spec coverage:** rules A–C → Task 1; the filesystem anchor → Task 2; rule D's properties →
Task 3; pipeline placement and `ExcludedArtifacts` → Task 4; the artifacts-layout and mirror-root
assertion → Task 5; both summary lines → Task 6; FR-15, FR-7 and DESIGN.md → Task 7.

**Deviation from the spec, deliberate:** the spec put the CLI assertions under "CLI tests", but no
CLI test project exists. Task 6 introduces `Report.fs` so the formatting is pure and unit-testable
rather than adding a process-spawning harness for two strings.

**Deferred, per the spec:** the foreign-project-directory rule and the E2E bind-mount cache-key
verification. Neither is in this plan.
