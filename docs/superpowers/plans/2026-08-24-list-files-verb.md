# `list-files` Verb Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add `dotnet isolate list-files <path>...` — a new verb that prints a project's resolved
dependency file set (same filtering as a real isolate run) to stdout without writing anything to
disk, for CI cache-skip checks — and move the CLI from one implicit action to two explicit verbs
(`materialize`, `list-files`) with no back-compat shim.

**Architecture:** Extract the shared project-graph/file-resolution/FR-15/FR-16 prefix of
`Pipeline.isolate` into a private `resolve` function; add `Pipeline.listFiles` on top of it. Split
`Report.warnings` into a shared `commonWarnings` helper plus two thin callers. Restructure
`Program.fs`'s Argu `Arguments` union into two subcommand cases dispatching to `Pipeline.isolate`
and `Pipeline.listFiles` respectively.

**Tech Stack:** F#, .NET 8, Argu 6.2.5, xUnit.

Spec: `docs/superpowers/specs/2026-08-24-list-files-verb-design.md`

## Global Constraints

- Run every command from `src/DotnetIsolate/` (the solution root).
- Build and the full test suite must pass before each commit (`dotnet build -c Release`, then
  `dotnet test -c Release --no-build`).
- Commit straight to `main` in small, verified steps — this project is trunk-based, no feature
  branches (per AGENTS.md and this user's standing preference).
- Source is F#: modules, pipelines, immutable data — no C#-style OOP.
- Repository files are LF-only (`.gitattributes`); never let an edit introduce CRLF.
- Never hardcode a path string like `"/repo/src/A"` in a test. Use
  `DotnetIsolate.UnitTests.PathHelpers.path [ "repo"; "src"; "A" ]` (unit tests) or the real
  temp-dir fixtures in `DotnetIsolate.IntegrationTests.TestFixtures` (integration tests).
- Never assert on a resolved path's exact string spelling — compare via `Path.GetFileName`, or
  `MirrorRoot.sameDirectory` for directories, since MSBuild's own casing/separators aren't the
  point under test.
- Argu subcommand case names that are shared between `MaterializeArgs` and `ListFilesArgs`
  (`Project_Paths`, `Output_Dir`, `Solution`, `Clean`, `Restore`) must be fully qualified
  (`MaterializeArgs.Clean`, `ListFilesArgs.Clean`, ...) everywhere they're used *outside* each
  type's own `Usage` member — verified directly against Argu 6.2.5 with a throwaway probe project;
  unqualified use outside the defining type is ambiguous and won't compile.

---

## Task 1: `Pipeline.listFiles`, extracted from a shared `resolve`

**Files:**
- Modify: `src/DotnetIsolate/DotnetIsolate.Core/Pipeline.fs`
- Test: `src/DotnetIsolate/DotnetIsolate.IntegrationTests/PipelineTests.fs`

**Interfaces:**
- Produces: `Pipeline.ListFilesOptions` (`ProjectPaths: string list`, `SolutionPath: string option`,
  `RestoreOnly: bool`) and `Pipeline.ListFilesResult` (`Files: string list` — sorted, absolute;
  `Projects: string list`; `SolutionRoot: SolutionDiscovery.SolutionRoot option`;
  `ExcludedArtifacts: string list`; `ForeignProjectFiles: ForeignFiles.ForeignGroup list`;
  `MissingFiles: string list`; `EntriesOutsideDiscoveredSolution: string list`), and
  `Pipeline.listFiles : ListFilesOptions -> ListFilesResult`. `Pipeline.isolate` and
  `Pipeline.IsolateOptions`/`IsolateResult` keep their existing shapes and behavior unchanged.

- [ ] **Step 1: Write the failing integration tests**

Add to the end of `src/DotnetIsolate/DotnetIsolate.IntegrationTests/PipelineTests.fs` (after the
existing last test, `restore and full phases compute the mirror root from the same full file set`):

```fsharp
/// list-files must resolve exactly the file set a real isolate run would materialize - same
/// project graph, same implicit files, same FR-15 artifact filtering - just printed instead of
/// copied. Reuses the diamond fixture so the two can be compared directly.
[<Fact>]
let ``listFiles resolves the same file set isolate would materialize`` () =
    withTempDir (fun root ->
        writeProject (Path.Combine(root, "A")) "A" [ "B"; "C" ] []
        writeProject (Path.Combine(root, "B")) "B" [ "D" ] []
        writeProject (Path.Combine(root, "C")) "C" [ "D" ] []
        writeProject (Path.Combine(root, "D")) "D" [] [ "appsettings.json" ]

        File.WriteAllText(Path.Combine(root, "NuGet.config"), "<configuration/>")

        writeSolution
            (Path.Combine(root, "Fixture.sln"))
            [ "A", "A/A.fsproj"; "B", "B/B.fsproj"; "C", "C/C.fsproj"; "D", "D/D.fsproj" ]

        let outputDir = Path.Combine(root, "output")

        let isolateResult =
            Pipeline.isolate
                { ProjectPaths = [ Path.Combine(root, "A", "A.fsproj") ]
                  OutputDir = Some outputDir
                  SolutionPath = None
                  RestoreOnly = false
                  Clean = false }

        let listResult =
            Pipeline.listFiles
                { ProjectPaths = [ Path.Combine(root, "A", "A.fsproj") ]
                  SolutionPath = None
                  RestoreOnly = false }

        // Same source files resolved - the materialized tree's file count and the printed list's
        // length agree.
        Assert.Equal(isolateResult.FileCount, listResult.Files.Length)

        // Every file the isolate run actually placed appears, absolute, in the printed list -
        // including the implicit repo-level NuGet.config (FR-10).
        Assert.Contains(listResult.Files, fun (f: string) -> Path.GetFileName(f) = "A.fsproj")
        Assert.Contains(listResult.Files, fun (f: string) -> Path.GetFileName(f) = "D.fsproj")
        Assert.Contains(listResult.Files, fun (f: string) -> Path.GetFileName(f) = "appsettings.json")
        Assert.Contains(listResult.Files, fun (f: string) -> Path.GetFileName(f) = "NuGet.config")

        // Sorted, ordinally, for deterministic CI diffing.
        Assert.Equal<string list>(listResult.Files |> List.sortWith (fun a b -> System.String.CompareOrdinal(a, b)), listResult.Files)

        Assert.Equal(3, listResult.Projects.Length)
        Assert.True(listResult.SolutionRoot.IsSome))

/// --restore must narrow listFiles the same way it narrows isolate's materialized output: project
/// files and build files, not sources or content.
[<Fact>]
let ``listFiles with RestoreOnly narrows to the restore subset`` () =
    withTempDir (fun root ->
        writeProject (Path.Combine(root, "A")) "A" [ "B" ] [ "appsettings.json" ]
        writeProject (Path.Combine(root, "B")) "B" [] []
        File.WriteAllText(Path.Combine(root, "NuGet.config"), "<configuration/>")
        writeSolution (Path.Combine(root, "Fixture.sln")) [ "A", "A/A.fsproj"; "B", "B/B.fsproj" ]

        let result =
            Pipeline.listFiles
                { ProjectPaths = [ Path.Combine(root, "A", "A.fsproj") ]
                  SolutionPath = None
                  RestoreOnly = true }

        Assert.Contains(result.Files, fun (f: string) -> Path.GetFileName(f) = "A.fsproj")
        Assert.Contains(result.Files, fun (f: string) -> Path.GetFileName(f) = "B.fsproj")
        Assert.Contains(result.Files, fun (f: string) -> Path.GetFileName(f) = "NuGet.config")
        Assert.DoesNotContain(result.Files, fun (f: string) -> Path.GetFileName(f) = "Program.fs")
        Assert.DoesNotContain(result.Files, fun (f: string) -> Path.GetFileName(f) = "appsettings.json"))

/// Unlike isolate (FR-13), listFiles never writes an output directory, so there is nothing to
/// disambiguate a name for - multiple entry projects must not require one.
[<Fact>]
let ``listFiles accepts multiple entry projects without requiring an output directory`` () =
    withTempDir (fun root ->
        writeProject (Path.Combine(root, "A")) "A" [] []
        writeProject (Path.Combine(root, "B")) "B" [] []
        writeSolution (Path.Combine(root, "Fixture.sln")) [ "A", "A/A.fsproj"; "B", "B/B.fsproj" ]

        let result =
            Pipeline.listFiles
                { ProjectPaths =
                    [ Path.Combine(root, "A", "A.fsproj"); Path.Combine(root, "B", "B.fsproj") ]
                  SolutionPath = None
                  RestoreOnly = false }

        Assert.Equal(2, result.Projects.Length)
        Assert.Contains(result.Files, fun (f: string) -> Path.GetFileName(f) = "A.fsproj")
        Assert.Contains(result.Files, fun (f: string) -> Path.GetFileName(f) = "B.fsproj"))
```

- [ ] **Step 2: Run the tests to verify they fail to compile**

Run: `dotnet test DotnetIsolate.IntegrationTests/DotnetIsolate.IntegrationTests.fsproj -c Release --filter "FullyQualifiedName~listFiles"`
Expected: build error - `Pipeline.listFiles`/`Pipeline.ListFilesOptions` do not exist yet.

- [ ] **Step 3: Replace `Pipeline.fs` with the refactored version**

Replace the entire contents of `src/DotnetIsolate/DotnetIsolate.Core/Pipeline.fs` with:

```fsharp
module DotnetIsolate.Core.Pipeline

open System.Collections.Concurrent
open System.IO

type IsolateOptions =
    { /// One or more entry projects; their dependency closures are unioned.
      ProjectPaths: string list
      OutputDir: string option
      SolutionPath: string option
      /// Emit only the files `dotnet restore` needs (see Phase.fs), rather than the full set.
      RestoreOnly: bool
      /// Delete and recreate the output directory first, instead of merging into it.
      Clean: bool }

type IsolateResult =
    { OutputDir: string
      IncludedProjects: string list
      FileCount: int
      SolutionRoot: SolutionDiscovery.SolutionRoot option
      Strategy: LinkStrategy.Strategy
      /// Resolved inputs dropped because they live under the output directory - almost always a
      /// previous run's output swept up by MSBuild's implicit globs.
      ExcludedUnderOutput: string list
      /// Resolved inputs dropped because they are generated build artifacts rather than build
      /// inputs (FR-15) - a previous host-side build's `obj/`, `bin/`, `node_modules/`, and so on.
      ExcludedArtifacts: string list
      /// Resolved inputs owned by a project outside the closure, grouped by owning directory
      /// (FR-16). Reported, never dropped: they are usually an over-reaching glob, but may be a
      /// deliberate cross-project link, and only the author can tell the two apart.
      ForeignProjectFiles: ForeignFiles.ForeignGroup list
      /// Entries already in the output directory that this run did not produce.
      StaleEntries: string list
      /// Item-derived paths referenced by a project that do not exist on disk, dropped rather than
      /// failing the run - e.g. a `<None Include="..\.dockerignore"/>` whose target was itself
      /// excluded from the Docker build context.
      MissingFiles: string list
      /// Entry projects that do not live under the discovered solution's directory. Solution
      /// discovery only ever walks up from the first entry, so these were never checked against
      /// it: the generated scoped solution file may omit them even though their files were
      /// isolated, and any ancestor-globbed ceiling files (e.g. .editorconfig) above their own
      /// directory tree were never picked up either. Empty when no solution was found.
      EntriesOutsideDiscoveredSolution: string list }

/// `list-files` (FR-17): the resolved dependency set for one or more entry projects, printed
/// rather than materialized.
type ListFilesOptions =
    { ProjectPaths: string list
      SolutionPath: string option
      /// List only the files `dotnet restore` needs (see Phase.fs), rather than the full set.
      RestoreOnly: bool }

type ListFilesResult =
    { /// Sorted (ordinal), absolute paths - deterministic across runs and platforms so the list is
      /// safe to diff in CI.
      Files: string list
      Projects: string list
      SolutionRoot: SolutionDiscovery.SolutionRoot option
      ExcludedArtifacts: string list
      ForeignProjectFiles: ForeignFiles.ForeignGroup list
      MissingFiles: string list
      EntriesOutsideDiscoveredSolution: string list }

/// The output of steps 1-3 (DESIGN.md) plus FR-15/FR-16 filtering: the project graph, each
/// project's resolved files, implicit repo-level files, and the artifact/foreign-file
/// classification. Shared by `isolate` and `listFiles`, whose paths diverge only after this point
/// - one continues into mirror-root computation and materialization, the other stops here.
type private Resolved =
    { Projects: string list
      SolutionRoot: SolutionDiscovery.SolutionRoot option
      /// Post FR-15 filtering - generated build artifacts already dropped.
      Files: string list
      ExcludedArtifacts: string list
      ForeignProjectFiles: ForeignFiles.ForeignGroup list
      MissingFiles: string list
      EntriesOutsideDiscoveredSolution: string list }

/// `projectPaths` must already be `Path.GetFullPath`-normalized by the caller. Raises if
/// `projectPaths` is empty, or if nothing was resolved at all (both callers surface these as their
/// own top-level failure, so the message doesn't need to name which verb was running).
let private resolve (projectPaths: string list) (solutionPathOverride: string option) : Resolved =
    if List.isEmpty projectPaths then
        failwith "at least one project path is required"

    // Solution discovery walks up from the first entry project's directory only. When every entry
    // shares a solution (the common case) this finds it regardless of which entry happens to be
    // first. But there is no check that the other entries are actually members of that solution -
    // an entry living outside its directory tree is silently unvalidated here; see the
    // outsideDiscoveredSolution warning below for the consequence and how it is surfaced.
    let projectDir = Path.GetDirectoryName(List.head projectPaths)

    // Steps 1 & 2 both need MSBuild evaluation per project - ProjectReference to walk the graph,
    // the file item types plus the file-path properties (e.g. CodeAnalysisRuleSet) to resolve
    // files. Querying all of them in one `dotnet msbuild -getItem -getProperty` call per project
    // (memoized here) instead of one per concern keeps the number of MSBuild process spawns - the
    // pipeline's dominant cost, see PR-1 - at exactly one per project. A ConcurrentDictionary
    // because ProjectGraph.resolve evaluates each BFS level's projects in parallel.
    let evaluationCache = ConcurrentDictionary<string, Map<string, string list> * Map<string, string>>()
    let allItemTypes = "ProjectReference" :: FileResolutionIo.fileItemTypes
    let allPropertyNames =
        FileResolutionIo.filePathPropertyNames @ FileResolutionIo.outputDirectoryPropertyNames

    let evaluateCached (projectPath: string) =
        evaluationCache.GetOrAdd(projectPath, (fun p -> MsBuild.getItemsAndProperties p allItemTypes allPropertyNames))

    let getItemsCached (projectPath: string) : Map<string, string list> = evaluateCached projectPath |> fst
    let getPropertiesCached (projectPath: string) : Map<string, string> = evaluateCached projectPath |> snd

    let projectReferenceResolver: ProjectGraph.ProjectReferenceResolver =
        fun projectPath -> getItemsCached projectPath |> Map.tryFind "ProjectReference" |> Option.defaultValue []

    // Step 1: the transitive project graph - the union of every entry project's closure.
    let projects = ProjectGraph.resolveMany projectReferenceResolver projectPaths
    let projectDirs = projects |> List.map Path.GetDirectoryName

    // Step 3's solution discovery runs before step 2 because it needs no MSBuild evaluation (just
    // a walk up from the project directory) and step 2 needs its result: the solution root is the
    // ceiling that bounds MSBuild's ancestor-globbed items, .editorconfig above all - see
    // FileResolution.ancestorGlobbedItemTypes.
    let solutionRoot =
        SolutionDiscovery.resolveSolutionRoot
            SolutionDiscoveryIo.solutionFilesOnDisk
            solutionPathOverride
            projectDir

    let ceiling = solutionRoot |> Option.map (fun r -> r.Directory)

    // The consequence of the limitation noted above, made visible: an entry outside the
    // discovered solution's directory tree got its files isolated (steps 1-2 don't care where a
    // solution is), but a scoped solution file can only ever include projects that were already
    // members of the source solution - and ancestor-globbed ceiling files above this entry's own
    // tree were never resolved either, since `ceiling` only bounds the discovered solution's
    // directory.
    let entriesOutsideDiscoveredSolution =
        match solutionRoot with
        | Some root -> projectPaths |> List.filter (fun p -> not (MirrorRoot.isUnder root.Directory p))
        | None -> []

    // Step 2: each project's build-relevant files - reuses the items already fetched above.
    let resolvers =
        { FileResolutionIo.resolvers ceiling with
            GetItems = getItemsCached
            GetProperties = getPropertiesCached }

    let resolvedProjectFiles = FileResolution.resolveAllFiles resolvers projects
    let projectFiles = resolvedProjectFiles.Files

    // Step 3 (continued): the implicit repo-level files, up to the same ceiling.
    let implicitFiles =
        ImplicitFiles.resolveForProjects ImplicitFilesIo.filesOnDisk ceiling projectDirs

    let allFiles = (projectFiles @ implicitFiles) |> List.distinct

    // FR-15: drop generated build artifacts before anything downstream can see them. MSBuild has
    // no reason to distinguish them - a project whose directory contains other projects globs
    // their bin/obj in through the SDK's default items, and a hand-written `<None Include="**/*"/>`
    // bypasses DefaultItemExcludes entirely - so the filter works on resolved paths and covers
    // both mechanisms alike.
    let declaredOutputDirectories =
        projects
        |> List.collect (fun p -> FileResolution.resolveOutputDirectories (getPropertiesCached p) p)
        |> List.distinct

    let artifactPartition =
        BuildArtifacts.partition BuildArtifactsIo.containsProjectFile declaredOutputDirectories allFiles

    let allFiles = artifactPartition.Kept

    // FR-16, over the artifact-filtered set: a `bin/` file under a foreign project has already
    // been dropped above and must not be reported a second time under a different heading.
    let foreignProjectFiles =
        ForeignFiles.detect
            BuildArtifactsIo.containsProjectFile
            (projects |> List.map Path.GetDirectoryName)
            allFiles

    if List.isEmpty allFiles then
        let describedProjectPaths = String.concat ", " projectPaths
        failwith $"no build-relevant files were resolved for {describedProjectPaths}"

    { Projects = projects
      SolutionRoot = solutionRoot
      Files = allFiles
      ExcludedArtifacts = artifactPartition.Excluded
      ForeignProjectFiles = foreignProjectFiles
      MissingFiles = resolvedProjectFiles.MissingItemFiles
      EntriesOutsideDiscoveredSolution = entriesOutsideDiscoveredSolution }

/// Runs the full pipeline described in DESIGN.md (steps 1-7) end to end: resolves the project
/// graph and its files, locates the solution (FR-8) and implicit repo-level files, computes the
/// mirror root, decides the link strategy, materializes the output folder, and - when a solution
/// was found - generates a scoped copy of it. Returns enough to report what happened; console
/// output (e.g. which solution was used, per FR-8) is the CLI's job, not this function's.
let isolate (options: IsolateOptions) : IsolateResult =
    let projectPaths = options.ProjectPaths |> List.map Path.GetFullPath
    let resolved = resolve projectPaths options.SolutionPath

    // FR-6: output defaults to ./<ProjectName>, or an explicit -o/--output-dir path. With more
    // than one entry project there is no single name to default to, so an explicit output
    // directory becomes mandatory instead of guessing.
    //
    // Normalised exactly once, here, because every output-directory safeguard downstream compares
    // it against resolved file paths segment by segment. Path.GetFullPath preserves a trailing
    // separator (verified: Path.GetFullPath("out/") returns ".../out/"), and a trailing separator
    // splits into an empty segment that matches nothing - so `-o repo/`, which is simply what
    // shell tab-completion produces, used to switch off the exclusion of files under the output,
    // switch off the self-consumption check that depends on it, and hand a source tree straight to
    // --clean's Directory.Delete. MirrorRoot.isUnder now tolerates a trailing separator too; this
    // trim additionally keeps the value reported back to the user, and used to build paths under
    // the output, in one canonical spelling.
    let outputDir =
        let raw =
            match options.OutputDir, projectPaths with
            | Some dir, _ -> Path.GetFullPath(dir)
            | None, [ single ] ->
                let name = Path.GetFileNameWithoutExtension(single)
                Path.GetFullPath(if options.RestoreOnly then $"{name}.restore" else name)
            | None, _ ->
                failwith
                    "an explicit output directory (-o/--output-dir) is required when more than one entry project is given"

        Path.TrimEndingDirectorySeparator(raw)

    // A previous run's output is indistinguishable from source to MSBuild's implicit globs, so
    // drop anything under the output directory before it can inflate the mirror root or get
    // placed inside itself.
    let partition = OutputSafety.partitionInputs outputDir resolved.Files

    match OutputSafety.validate options.Clean outputDir partition with
    | Error message -> failwith message
    | Ok() -> ()

    let allFiles = partition.Kept

    // Step 4: the mirror root - includes the solution root directory itself (see DESIGN.md) so
    // the generated solution file in step 7 always lands inside the output folder.
    let mirrorRootInputDirs =
        (allFiles |> List.map Path.GetDirectoryName)
        @ (resolved.SolutionRoot |> Option.map (fun r -> [ r.Directory ]) |> Option.defaultValue [])

    let mirrorRoot =
        match MirrorRoot.compute mirrorRootInputDirs with
        | Some root -> root
        | None -> failwith "could not compute a mirror root"

    // The restore phase is applied after the mirror root is computed from the full set, so both
    // phases share one root and their outputs overlay exactly - which is what lets a Dockerfile
    // COPY the restore half, run `dotnet restore`, then COPY the full half over it.
    let filesToPlace =
        if options.RestoreOnly then
            Phase.restoreSubset resolved.Projects allFiles
        else
            allFiles

    // Checked before the probe below, which needs a real file to link. The full set is already
    // known to be non-empty, but Phase.restoreSubset narrows it, so an entry project whose subset
    // came back empty would otherwise surface as F#'s bare "The input list was empty" - a message
    // that names neither the flag nor the projects responsible.
    if List.isEmpty filesToPlace then
        let describedProjectPaths = String.concat ", " projectPaths

        failwith
            $"the --restore phase resolved no files to emit for {describedProjectPaths}; nothing would be written"

    // Step 5: decide the link strategy once, via a real file already in the resolved set.
    let strategy = LinkStrategy.probe (List.head filesToPlace) outputDir

    // Step 6: materialize the output folder, capturing pre-existing entries this run didn't
    // produce so the CLI can report them rather than silently deleting or ignoring them.
    let staleEntries = Materialize.materialize strategy options.Clean mirrorRoot outputDir filesToPlace

    // Step 7: generate the scoped solution file, if a source solution was found.
    match resolved.SolutionRoot with
    | Some root when root.SolutionFile.EndsWith(".sln") ->
        let sourceContent = File.ReadAllText(root.SolutionFile)
        let filtered = SolutionFile.filterSln root.Directory (Set.ofList resolved.Projects) sourceContent
        let relativeSlnPath = Path.GetRelativePath(mirrorRoot, root.SolutionFile)
        let outputSlnPath = Path.Combine(outputDir, relativeSlnPath)
        Directory.CreateDirectory(Path.GetDirectoryName(outputSlnPath)) |> ignore
        SolutionFileIo.write outputSlnPath filtered
    | Some root when root.SolutionFile.EndsWith(".slnx") ->
        let sourceContent = File.ReadAllText(root.SolutionFile)
        let filtered = SolutionFileXml.filterSlnx root.Directory (Set.ofList resolved.Projects) sourceContent
        let relativeSlnPath = Path.GetRelativePath(mirrorRoot, root.SolutionFile)
        let outputSlnPath = Path.Combine(outputDir, relativeSlnPath)
        Directory.CreateDirectory(Path.GetDirectoryName(outputSlnPath)) |> ignore
        SolutionFileXmlIo.write outputSlnPath filtered
    | Some root ->
        // Solution discovery only ever finds .sln/.slnx, so this is unreachable in practice.
        ignore root
    | None -> ()

    { OutputDir = outputDir
      IncludedProjects = resolved.Projects
      FileCount = filesToPlace.Length
      SolutionRoot = resolved.SolutionRoot
      Strategy = strategy
      ExcludedUnderOutput = partition.ExcludedUnderOutput
      ExcludedArtifacts = resolved.ExcludedArtifacts
      ForeignProjectFiles = resolved.ForeignProjectFiles
      StaleEntries = staleEntries
      MissingFiles = resolved.MissingFiles
      EntriesOutsideDiscoveredSolution = resolved.EntriesOutsideDiscoveredSolution }

/// FR-17: the same resolved dependency set `isolate` would materialize, printed instead - no
/// mirror root, no materialization, no solution-file generation. `--restore` narrows it to the
/// same subset `isolate --restore` writes. Unlike `isolate`, multiple entry projects never require
/// an explicit output directory, since there is no output directory at all.
let listFiles (options: ListFilesOptions) : ListFilesResult =
    let projectPaths = options.ProjectPaths |> List.map Path.GetFullPath
    let resolved = resolve projectPaths options.SolutionPath

    let files =
        if options.RestoreOnly then
            Phase.restoreSubset resolved.Projects resolved.Files
        else
            resolved.Files

    // Mirrors isolate's analogous check on filesToPlace: the full set is already known to be
    // non-empty (resolve's own check above), but the restore narrowing can still empty it out.
    if List.isEmpty files then
        let describedProjectPaths = String.concat ", " projectPaths

        failwith
            $"the --restore phase resolved no files to list for {describedProjectPaths}; nothing would be printed"

    let sortedFiles = files |> List.sortWith (fun a b -> System.String.CompareOrdinal(a, b))

    { Files = sortedFiles
      Projects = resolved.Projects
      SolutionRoot = resolved.SolutionRoot
      ExcludedArtifacts = resolved.ExcludedArtifacts
      ForeignProjectFiles = resolved.ForeignProjectFiles
      MissingFiles = resolved.MissingFiles
      EntriesOutsideDiscoveredSolution = resolved.EntriesOutsideDiscoveredSolution }
```

- [ ] **Step 4: Run the full existing `Pipeline.isolate` test suite to confirm no regression**

Run: `dotnet build -c Release` then
`dotnet test DotnetIsolate.IntegrationTests/DotnetIsolate.IntegrationTests.fsproj -c Release --no-build --filter "FullyQualifiedName~PipelineTests"`
Expected: PASS - every existing `isolate`-based test in `PipelineTests.fs` still passes unchanged,
since the refactor moves code without changing `isolate`'s behavior.

- [ ] **Step 5: Run the new `listFiles` tests to verify they pass**

Run: `dotnet test DotnetIsolate.IntegrationTests/DotnetIsolate.IntegrationTests.fsproj -c Release --no-build --filter "FullyQualifiedName~listFiles"`
Expected: PASS for all three new tests.

- [ ] **Step 6: Commit**

```bash
git add DotnetIsolate.Core/Pipeline.fs DotnetIsolate.IntegrationTests/PipelineTests.fs
git commit -m "feat: add Pipeline.listFiles, extracted from a shared resolve step (FR-17)"
```

---

## Task 2: FR-15/FR-16 parity for `listFiles`

**Files:**
- Modify: `src/DotnetIsolate/DotnetIsolate.IntegrationTests/BuildArtifactExclusionTests.fs`
- Modify: `src/DotnetIsolate/DotnetIsolate.IntegrationTests/ForeignProjectFileTests.fs`

**Interfaces:**
- Consumes: `Pipeline.listFiles`, `Pipeline.ListFilesOptions`, `Pipeline.ListFilesResult` (Task 1).

- [ ] **Step 1: Write the failing FR-15 parity test**

Add to the end of `BuildArtifactExclusionTests.fs`:

```fsharp
/// listFiles must apply the same FR-15 filtering isolate does: an artifact is neither printed nor
/// silently missing - it's reported via ExcludedArtifacts, same as isolate reports it.
[<Fact>]
let ``listFiles excludes bin and obj artifacts of nested projects, same as isolate`` () =
    withTempDir (fun root ->
        writeProject (Path.Combine(root, "A")) "A" [ "B" ] []
        writeProject (Path.Combine(root, "B")) "B" [] []

        let objDir = Path.Combine(root, "B", "obj")
        Directory.CreateDirectory(objDir) |> ignore
        File.WriteAllText(Path.Combine(objDir, "project.assets.json"), "{}")

        let result =
            Pipeline.listFiles
                { ProjectPaths = [ Path.Combine(root, "A", "A.fsproj") ]
                  SolutionPath = None
                  RestoreOnly = false }

        Assert.DoesNotContain(result.Files, fun (f: string) -> Path.GetFileName(f) = "project.assets.json")
        Assert.Contains(result.ExcludedArtifacts, fun (f: string) -> Path.GetFileName(f) = "project.assets.json"))
```

- [ ] **Step 2: Write the failing FR-16 parity test**

Add to the end of `ForeignProjectFileTests.fs`:

```fsharp
/// listFiles must keep a foreign-owned file in the printed list (it's a real build input) while
/// still reporting it via ForeignProjectFiles - same as isolate, which keeps it materialized.
[<Fact>]
let ``listFiles reports a file owned by a project outside the closure without dropping it`` () =
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
    <None Include="../Foreign/**/*.json"/>
  </ItemGroup>
</Project>
"""
        )

        File.WriteAllText(Path.Combine(projectDir, "Program.fs"), "module A.Program\n")

        writeProject (Path.Combine(root, "Foreign")) "Foreign" [] []
        File.WriteAllText(Path.Combine(root, "Foreign", "swept.json"), "{}")

        let result =
            Pipeline.listFiles
                { ProjectPaths = [ Path.Combine(projectDir, "A.fsproj") ]
                  SolutionPath = None
                  RestoreOnly = false }

        let group = result.ForeignProjectFiles |> List.exactlyOne
        Assert.True(MirrorRoot.sameDirectory (Path.Combine(root, "Foreign")) group.Directory)
        Assert.Contains(result.Files, fun (f: string) -> Path.GetFileName(f) = "swept.json"))
```

- [ ] **Step 3: Run both new tests to verify they pass**

Run: `dotnet test DotnetIsolate.IntegrationTests/DotnetIsolate.IntegrationTests.fsproj -c Release --no-build --filter "FullyQualifiedName~listFiles"`
Expected: PASS for both.

- [ ] **Step 4: Run the full integration suite**

Run: `dotnet test DotnetIsolate.IntegrationTests/DotnetIsolate.IntegrationTests.fsproj -c Release --no-build`
Expected: PASS, no regressions.

- [ ] **Step 5: Commit**

```bash
git add DotnetIsolate.IntegrationTests/BuildArtifactExclusionTests.fs DotnetIsolate.IntegrationTests/ForeignProjectFileTests.fs
git commit -m "test: pin FR-15/FR-16 parity between listFiles and isolate"
```

---

## Task 3: Split `Report.warnings` for `listFiles`

**Files:**
- Modify: `src/DotnetIsolate/DotnetIsolate.Core/Report.fs`
- Test: `src/DotnetIsolate/DotnetIsolate.UnitTests/ReportTests.fs`

**Interfaces:**
- Consumes: `Pipeline.ListFilesResult` (Task 1).
- Produces: `Report.listFilesWarnings : Pipeline.ListFilesResult -> string list`. `Report.warnings :
  Pipeline.IsolateResult -> string list` keeps its existing signature; its output content is
  unchanged but the **order** of its lines changes slightly (see Step 3 note) - no existing test
  asserts on line order, only content and count, so this is safe.

- [ ] **Step 1: Write the failing unit tests**

Add to the end of `src/DotnetIsolate/DotnetIsolate.UnitTests/ReportTests.fs`:

```fsharp
/// A ListFilesResult with nothing to report; tests set only the field they are about.
let private cleanListFiles: Pipeline.ListFilesResult =
    { Files = []
      Projects = []
      SolutionRoot = None
      ExcludedArtifacts = []
      ForeignProjectFiles = []
      MissingFiles = []
      EntriesOutsideDiscoveredSolution = [] }

[<Fact>]
let ``no warnings are produced for a clean list-files run`` () =
    Assert.Empty(Report.listFilesWarnings cleanListFiles)

[<Fact>]
let ``list-files warnings summarise excluded artifacts the same way isolate does`` () =
    let result =
        { cleanListFiles with
            ExcludedArtifacts = [ path [ "a" ]; path [ "b" ]; path [ "c" ] ] }

    let line = Report.listFilesWarnings result |> List.exactlyOne

    Assert.Contains("3", line)
    Assert.Contains("build artifact", line)

[<Fact>]
let ``list-files warnings report foreign project files the same way isolate does`` () =
    let other = path [ "repo"; "Other" ]
    let first = path [ "repo"; "Other"; "a.cs" ]

    let group: ForeignFiles.ForeignGroup = { Directory = other; Files = [ first ] }

    let result = { cleanListFiles with ForeignProjectFiles = [ group ] }
    let line = Report.listFilesWarnings result |> List.exactlyOne

    Assert.Contains("1 file(s)", line)
    Assert.Contains(other, line)
```

- [ ] **Step 2: Run the tests to verify they fail to compile**

Run: `dotnet test DotnetIsolate.UnitTests/DotnetIsolate.UnitTests.fsproj -c Release --filter "FullyQualifiedName~list-files"`
Expected: build error - `Report.listFilesWarnings` does not exist yet.

- [ ] **Step 3: Replace `Report.fs` with the split version**

Replace the entire contents of `src/DotnetIsolate/DotnetIsolate.Core/Report.fs` with:

```fsharp
module DotnetIsolate.Core.Report

/// The FR-14/15/16 diagnostic lines shared by both verbs: artifact-exclusion count, foreign-
/// project-file groups, missing item files, and entries outside the discovered solution.
/// Formatting lives here rather than in Program.fs so it is unit-testable: the CLI has no test
/// project, and these strings are the tool's whole report of what it silently dropped or flagged.
let private commonWarnings
    (excludedArtifacts: string list)
    (foreignProjectFiles: ForeignFiles.ForeignGroup list)
    (missingFiles: string list)
    (entriesOutsideDiscoveredSolution: string list)
    (solutionRoot: SolutionDiscovery.SolutionRoot option)
    : string list =
    [ if not (List.isEmpty excludedArtifacts) then
          $"note: skipped {excludedArtifacts.Length} generated build artifact(s) (bin/, obj/, node_modules/, build and coverage logs)"

      for group in foreignProjectFiles do
          $"note: {group.Files.Length} file(s) resolved from {group.Directory}, whose project is not in the isolated closure"

      for entry in missingFiles do
          $"warning: {entry} is referenced by a project but does not exist; skipped"

      // solutionRoot is always Some here when this list is non-empty (see Pipeline.fs).
      for entry in entriesOutsideDiscoveredSolution do
          $"warning: {entry} lies outside the discovered solution ({solutionRoot.Value.SolutionFile}); the generated solution file may not include it" ]

/// The warning lines an `isolate` run should print to stderr, in order.
///
/// Two of the categories are summarised rather than listed. Artifact exclusions (FR-15) and stale
/// output entries (FR-7) arrive in bulk - hundreds of lines carrying one bit of information
/// between them - while a missing item file (FR-14) and a resolved input sitting under the output
/// directory are each rare and individually actionable, so those stay per file.
let warnings (result: Pipeline.IsolateResult) : string list =
    commonWarnings
        result.ExcludedArtifacts
        result.ForeignProjectFiles
        result.MissingFiles
        result.EntriesOutsideDiscoveredSolution
        result.SolutionRoot
    @ [ if not (List.isEmpty result.StaleEntries) then
            $"warning: the output directory was not clean ({result.StaleEntries.Length} pre-existing file(s) left in place); use --clean to replace it"

        for entry in result.ExcludedUnderOutput do
            $"warning: ignoring {entry}, which lives under the output directory" ]

/// The warning lines a `list-files` run should print to stderr (FR-17). Only the shared FR-14/15/
/// 16 categories apply - StaleEntries and ExcludedUnderOutput are materialization-only concerns
/// list-files never has, since it never writes an output directory.
let listFilesWarnings (result: Pipeline.ListFilesResult) : string list =
    commonWarnings
        result.ExcludedArtifacts
        result.ForeignProjectFiles
        result.MissingFiles
        result.EntriesOutsideDiscoveredSolution
        result.SolutionRoot
```

- [ ] **Step 4: Run the full unit test suite**

Run: `dotnet build -c Release` then
`dotnet test DotnetIsolate.UnitTests/DotnetIsolate.UnitTests.fsproj -c Release --no-build`
Expected: PASS - all existing `ReportTests` still pass (they assert content/count per category, not
cross-category order), plus the three new tests.

- [ ] **Step 5: Commit**

```bash
git add DotnetIsolate.Core/Report.fs DotnetIsolate.UnitTests/ReportTests.fs
git commit -m "refactor: split Report.warnings into a shared helper plus listFilesWarnings"
```

---

## Task 4: CLI verbs — `materialize` and `list-files`

**Files:**
- Modify: `src/DotnetIsolate/DotnetIsolate/Program.fs`

**Interfaces:**
- Consumes: `Pipeline.isolate`, `Pipeline.IsolateOptions`, `Pipeline.listFiles`,
  `Pipeline.ListFilesOptions`, `Report.warnings`, `Report.listFilesWarnings` (Tasks 1 & 3).

No existing test project covers `Program.fs` directly (only the E2E suite shells out to the built
executable - that's Task 5). Correctness here is verified by a clean build and a manual dogfood run
in Step 4.

- [ ] **Step 1: Replace `Program.fs`**

Replace the entire contents of `src/DotnetIsolate/DotnetIsolate/Program.fs` with:

```fsharp
module Program

open Argu
open DotnetIsolate.Core

type MaterializeArgs =
    | [<MainCommand; ExactlyOnce>] Project_Paths of paths: string list
    | [<AltCommandLine("-o")>] Output_Dir of dir: string
    | [<AltCommandLine("-s")>] Solution of path: string
    | Clean
    | Restore

    interface IArgParserTemplate with
        member this.Usage =
            match this with
            | Project_Paths _ -> "one or more .csproj/.fsproj files to isolate"
            | Output_Dir _ -> "output directory (default: ./<ProjectName>)"
            | Solution _ -> "solution file to use, overriding auto-discovery (FR-8)"
            | Clean -> "delete and recreate the output directory instead of merging into it"
            | Restore -> "emit only the files `dotnet restore` needs, for a cacheable Docker restore layer"

type ListFilesArgs =
    | [<MainCommand; ExactlyOnce>] Project_Paths of paths: string list
    | [<AltCommandLine("-s")>] Solution of path: string
    | Restore
    // Declared, but rejected in `runListFiles` below: list-files never writes an output directory,
    // so neither applies. Declared (not omitted) because Argu's MainCommand silently folds
    // unrecognized flags into Project_Paths rather than rejecting them - verified directly against
    // Argu 6.2.5 - so omitting these would turn a stray "-o" into a bogus extra project path
    // instead of a clear error. See docs/superpowers/specs/2026-08-24-list-files-verb-design.md.
    | [<AltCommandLine("-o")>] Output_Dir of dir: string
    | Clean

    interface IArgParserTemplate with
        member this.Usage =
            match this with
            | Project_Paths _ -> "one or more .csproj/.fsproj files to list dependencies for"
            | Solution _ -> "solution file to use, overriding auto-discovery (FR-8)"
            | Restore -> "list only the files `dotnet restore` needs"
            | Output_Dir _ -> "not valid with list-files; it never writes an output directory"
            | Clean -> "not valid with list-files; it never writes an output directory"

type Arguments =
    | [<CliPrefix(CliPrefix.None)>] Materialize of ParseResults<MaterializeArgs>
    | [<CliPrefix(CliPrefix.None)>] List_Files of ParseResults<ListFilesArgs>

    interface IArgParserTemplate with
        member this.Usage =
            match this with
            | Materialize _ -> "resolve the project graph and copy it into an isolated output folder"
            | List_Files _ -> "print the resolved file set without copying anything (FR-17)"

let private describeSolutionSource (source: SolutionDiscovery.SolutionRootSource) =
    match source with
    | SolutionDiscovery.ExplicitlyProvided -> "explicitly provided"
    | SolutionDiscovery.AutoDiscovered -> "auto-discovered"

let private runMaterialize (args: ParseResults<MaterializeArgs>) : int =
    let result =
        Pipeline.isolate
            { ProjectPaths = args.GetResult(MaterializeArgs.Project_Paths)
              OutputDir = args.TryGetResult(MaterializeArgs.Output_Dir)
              SolutionPath = args.TryGetResult(MaterializeArgs.Solution)
              RestoreOnly = args.Contains(MaterializeArgs.Restore)
              Clean = args.Contains(MaterializeArgs.Clean) }

    // FR-8: always report which solution was used, since more than one solution can reference the
    // same project and the choice isn't always obvious.
    match result.SolutionRoot with
    | Some root -> printfn $"Using solution ({describeSolutionSource root.Source}): {root.SolutionFile}"
    | None -> printfn "No solution file found; skipping solution generation."

    printfn
        $"Isolated {result.IncludedProjects.Length} project(s), {result.FileCount} file(s) into {result.OutputDir}"

    printfn $"Link strategy: {result.Strategy}"

    for warning in Report.warnings result do
        eprintfn $"{warning}"

    0

/// FR-17. Deliberate output-channel discipline: stdout carries only the file list, one absolute
/// path per line, sorted - nothing else - so a CI script can pipe it straight into a diff without
/// filtering. Everything else (which solution was used, FR-14/15/16 diagnostics) goes to stderr.
let private runListFiles (args: ParseResults<ListFilesArgs>) : int =
    if args.Contains(ListFilesArgs.Output_Dir) || args.Contains(ListFilesArgs.Clean) then
        eprintfn "Error: -o/--output-dir and --clean are not valid with list-files; it never writes an output directory."
        1
    else
        let result =
            Pipeline.listFiles
                { ProjectPaths = args.GetResult(ListFilesArgs.Project_Paths)
                  SolutionPath = args.TryGetResult(ListFilesArgs.Solution)
                  RestoreOnly = args.Contains(ListFilesArgs.Restore) }

        match result.SolutionRoot with
        | Some root -> eprintfn $"Using solution ({describeSolutionSource root.Source}): {root.SolutionFile}"
        | None -> eprintfn "No solution file found."

        for warning in Report.listFilesWarnings result do
            eprintfn $"{warning}"

        for file in result.Files do
            printfn $"{file}"

        0

[<EntryPoint>]
let main argv =
    let parser = ArgumentParser.Create<Arguments>(programName = "dotnet-isolate")

    try
        let results = parser.ParseCommandLine(argv)

        match results.GetSubCommand() with
        | Materialize sub -> runMaterialize sub
        | List_Files sub -> runListFiles sub
    with
    | :? ArguParseException as ex ->
        eprintfn $"{ex.Message}"
        1
    | ex ->
        eprintfn $"Error: {ex.Message}"
        1
```

- [ ] **Step 2: Build**

Run: `dotnet build -c Release`
Expected: builds cleanly with no warnings about ambiguous case names (fully qualified per the
Global Constraints note).

- [ ] **Step 3: Dogfood — materialize**

Run (from `src/DotnetIsolate/`):
`dotnet run --project DotnetIsolate -c Release -- materialize DotnetIsolate.Core/DotnetIsolate.Core.fsproj -o /tmp/dogfood-materialize --clean`
Expected: exits 0, prints "Using solution (auto-discovered): .../DotnetIsolate.sln", an "Isolated N
project(s), M file(s)..." line, and a "Link strategy: ..." line; `/tmp/dogfood-materialize` contains
a mirrored `DotnetIsolate.Core/` directory and a filtered `.sln`.

- [ ] **Step 4: Dogfood — list-files**

Run:
`dotnet run --project DotnetIsolate -c Release -- list-files DotnetIsolate.Core/DotnetIsolate.Core.fsproj`
Expected: exits 0; stdout is only absolute file paths, one per line, sorted; the "Using solution"
line appears on stderr, not mixed into stdout. Then verify the rejection:
`dotnet run --project DotnetIsolate -c Release -- list-files DotnetIsolate.Core/DotnetIsolate.Core.fsproj -o /tmp/x`
Expected: exits 1 with the "not valid with list-files" error message, nothing written to
`/tmp/x`.

- [ ] **Step 5: Clean up dogfood output and commit**

```bash
rm -rf /tmp/dogfood-materialize
git add DotnetIsolate/Program.fs
git commit -m "feat: restructure the CLI into materialize/list-files verbs (FR-17)"
```

---

## Task 5: Update the E2E Docker suite for the `materialize` verb

**Files:**
- Modify: `src/DotnetIsolate/DotnetIsolate.E2ETests/Fixtures/SelfContained/Dockerfile`
- Modify: `src/DotnetIsolate/DotnetIsolate.E2ETests/Fixtures/TwoPhase/Dockerfile`
- Modify: `src/DotnetIsolate/DotnetIsolate.E2ETests/DockerCacheTests.fs`

**Interfaces:** None - these tests shell out to the built `dotnet-isolate` executable and to real
`docker build`, so this task only updates literal command strings to match Task 4's new CLI shape.
Requires a Docker daemon; per AGENTS.md this suite isn't part of the coverage gates and doesn't run
as part of a bare `dotnet test` without Docker available - run it only if Docker is present in this
environment.

- [ ] **Step 1: Update the SelfContained Dockerfile**

In `src/DotnetIsolate/DotnetIsolate.E2ETests/Fixtures/SelfContained/Dockerfile`, change:

```dockerfile
RUN dotnet isolate ServiceA/ServiceA.csproj -o /isolated
```

to:

```dockerfile
RUN dotnet isolate materialize ServiceA/ServiceA.csproj -o /isolated
```

- [ ] **Step 2: Update the TwoPhase Dockerfile**

In `src/DotnetIsolate/DotnetIsolate.E2ETests/Fixtures/TwoPhase/Dockerfile`, change:

```dockerfile
RUN --mount=type=bind,target=/src,source=. \
    dotnet isolate /src/ServiceA/ServiceA.csproj --restore -o /isolated/restore
RUN --mount=type=bind,target=/src,source=. \
    dotnet isolate /src/ServiceA/ServiceA.csproj -o /isolated/full
```

to:

```dockerfile
RUN --mount=type=bind,target=/src,source=. \
    dotnet isolate materialize /src/ServiceA/ServiceA.csproj --restore -o /isolated/restore
RUN --mount=type=bind,target=/src,source=. \
    dotnet isolate materialize /src/ServiceA/ServiceA.csproj -o /isolated/full
```

- [ ] **Step 3: Update the matching needle string in `DockerCacheTests.fs`**

In `src/DotnetIsolate/DotnetIsolate.E2ETests/DockerCacheTests.fs`, change:

```fsharp
let private isolateStepNeedle =
    "dotnet isolate /src/ServiceA/ServiceA.csproj -o /isolated/full"
```

to:

```fsharp
let private isolateStepNeedle =
    "dotnet isolate materialize /src/ServiceA/ServiceA.csproj -o /isolated/full"
```

Leave line 59's `stepNotCached true output2 "dotnet isolate"` unchanged - `"dotnet isolate"` is
still a valid substring of `"dotnet isolate materialize ..."`, so that check still matches. The
HostSide Dockerfile and its `Pipeline.isolate` in-process call (`self-contained pattern` and
`host-side pattern` tests) are untouched by this task: the host-side harness calls
`Pipeline.isolate` directly (Task 1 kept its signature unchanged), and the HostSide Dockerfile never
invokes the CLI at all.

- [ ] **Step 4: Run the E2E suite, if Docker is available**

Run: `dotnet build -c Release` then
`dotnet test DotnetIsolate.E2ETests/DotnetIsolate.E2ETests.fsproj -c Release --no-build`
Expected: PASS if a Docker daemon is reachable in this environment; if not, note in the final
summary that this suite could not be verified locally (per AGENTS.md's guidance on unverifiable
path/tooling changes) and rely on CI's `e2e` job.

- [ ] **Step 5: Commit**

```bash
git add DotnetIsolate.E2ETests/Fixtures/SelfContained/Dockerfile DotnetIsolate.E2ETests/Fixtures/TwoPhase/Dockerfile DotnetIsolate.E2ETests/DockerCacheTests.fs
git commit -m "test: update E2E Dockerfiles and needles for the materialize verb"
```

---

## Task 6: Documentation — REQUIREMENTS.md, README.md, AGENTS.md

**Files:**
- Modify: `REQUIREMENTS.md`
- Modify: `README.md`
- Modify: `AGENTS.md`

**Interfaces:** None - documentation only.

- [ ] **Step 1: Add FR-17 to `REQUIREMENTS.md`**

Insert immediately after FR-16's last line (`... Reported per owning directory with a count, since
one glob sweeps many files.`, currently the line right before the `## Performance` heading) and
before that heading, as a new paragraph in the same Functional Requirements section:

```markdown

- **FR-17** — `list-files` prints the same resolved file set `materialize` would place into an
  output directory — after FR-15 artifact filtering, including FR-16 foreign-project files — as one
  absolute path per line on stdout, sorted, without writing anything to disk. Intended for a CI step
  that compares the list against `git diff --name-only` to decide whether a service's dependencies
  actually changed, and skip an otherwise-identical rebuild when they didn't. `--restore` narrows
  the list to the FR-11 restore subset. Unlike `materialize`'s FR-13 requirement, multiple entry
  projects never require an explicit output directory, since there is none to name. Solution-source
  and FR-14/15/16 diagnostics go to stderr, never stdout, so the file list is safe to consume
  directly.
```

- [ ] **Step 2: Update README.md's Usage section and add the CI cache-skip recipe**

Replace:

```markdown
## Usage

    dotnet isolate path/to/<Project>.csproj [more.csproj ...] [-o <dir>] [-s <path>] [--restore] [--clean]

- one or more entry projects; the output is the union of their closures
- `-o`/`--output-dir` — where to write (default `./<ProjectName>`); required for multiple projects
- `-s`/`--solution` — the solution to scope against, overriding the walk-up search
- `--restore` — emit only the files `dotnet restore` reads, for the split below
- `--clean` — delete the output directory first, instead of merging into it
```

with:

```markdown
## Usage

    dotnet isolate materialize path/to/<Project>.csproj [more.csproj ...] [-o <dir>] [-s <path>] [--restore] [--clean]
    dotnet isolate list-files  path/to/<Project>.csproj [more.csproj ...] [-s <path>] [--restore]

`materialize` copies the isolated closure into an output folder — this is what earlier versions did
implicitly. `list-files` prints the same resolved file set instead, one absolute path per line on
stdout, without writing anything (see "Skipping a rebuild when nothing changed" below).

- one or more entry projects; the result is the union of their closures
- `-o`/`--output-dir` (materialize only) — where to write (default `./<ProjectName>`); required for
  multiple projects
- `-s`/`--solution` — the solution to scope against, overriding the walk-up search
- `--restore` — emit/list only the files `dotnet restore` reads, for the split below
- `--clean` (materialize only) — delete the output directory first, instead of merging into it
```

Then, after the existing "How this keeps a Docker build cached" section (which ends with "...
`COPY` and `publish` paths."), append a new section:

```markdown

## Skipping a rebuild when nothing changed

`list-files` prints a service's exact dependency set — the same one `materialize` would place into
an output directory — so a CI step can skip an unchanged service's build/test stage entirely
instead of relying on coarse path filters (e.g. "did anything under src/Service1/ change", which
misses a shared library elsewhere in the closure and over-triggers on files the build never reads).

Paths are printed absolute; `git diff --name-only` reports repo-relative paths, so the recipe
relativizes before comparing:

    repo_root=$(git rev-parse --show-toplevel)
    deps=$(dotnet isolate list-files src/Service1/Service1.csproj | sed "s|^$repo_root/||" | sort)
    changed=$(git diff --name-only "$BASE_SHA" HEAD | sort)

    if comm -12 <(printf '%s\n' "$deps") <(printf '%s\n' "$changed") | grep -q .; then
      echo "dependencies changed — build required"
    else
      echo "no dependency changes — skipping build"
    fi

`BASE_SHA` is whatever your CI provider exposes for "the commit this branch diverged from" — e.g.
the merge-base with the target branch for a pull request.
```

- [ ] **Step 3: Update `AGENTS.md`'s Usage line**

In `AGENTS.md`, replace:

```markdown
Usage:

    dotnet isolate path/to/<Project>.csproj [-o/--output-dir <name>] [-s/--solution <path/to/.sln(x)>]
```

with:

```markdown
Usage:

    dotnet isolate materialize path/to/<Project>.csproj [-o/--output-dir <name>] [-s/--solution <path/to/.sln(x)>]
    dotnet isolate list-files  path/to/<Project>.csproj [-s/--solution <path/to/.sln(x)>]

`list-files` (FR-17) prints the resolved dependency set instead of copying it, for CI cache-skip
checks — see README.md's "Skipping a rebuild when nothing changed".
```

- [ ] **Step 4: Verify the docs build/render sensibly**

There's no automated doc build; visually re-read the three edited files end to end for internal
consistency (no leftover reference to the old verbless `dotnet isolate <path>` form anywhere in
these three files).

Run: `grep -rn "dotnet isolate [^m l]" README.md AGENTS.md REQUIREMENTS.md` (a quick sweep for any
remaining verbless invocation the two edits above missed — matches `dotnet isolate ` NOT followed by
`m` or `l`, which "materialize"/"list-files" both start with).
Expected: no output (or only unrelated matches, e.g. narrative prose mentioning "dotnet isolate" the
tool by name rather than as a command line).

- [ ] **Step 5: Commit**

```bash
git add REQUIREMENTS.md README.md AGENTS.md
git commit -m "docs: document the list-files verb and materialize's rename (FR-17)"
```
