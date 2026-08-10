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
      /// Entries already in the output directory that this run did not produce.
      StaleEntries: string list
      /// Item-derived paths referenced by a project that do not exist on disk, dropped rather than
      /// failing the run - e.g. a `<None Include="..\.dockerignore"/>` whose target was itself
      /// excluded from the Docker build context.
      MissingFiles: string list }

/// Runs the full pipeline described in DESIGN.md (steps 1-7) end to end: resolves the project
/// graph and its files, locates the solution (FR-8) and implicit repo-level files, computes the
/// mirror root, decides the link strategy, materializes the output folder, and - when a solution
/// was found - generates a scoped copy of it. Returns enough to report what happened; console
/// output (e.g. which solution was used, per FR-8) is the CLI's job, not this function's.
let isolate (options: IsolateOptions) : IsolateResult =
    // Tasks 7 and 9 give ProjectPaths (multi-project unions) and RestoreOnly their real
    // behaviour; for now the first entry project is all that's consumed.
    let projectPath = Path.GetFullPath(List.head options.ProjectPaths)
    let projectDir = Path.GetDirectoryName(projectPath)

    // Steps 1 & 2 both need MSBuild evaluation per project - ProjectReference to walk the graph,
    // the file item types plus the file-path properties (e.g. CodeAnalysisRuleSet) to resolve
    // files. Querying all of them in one `dotnet msbuild -getItem -getProperty` call per project
    // (memoized here) instead of one per concern keeps the number of MSBuild process spawns - the
    // pipeline's dominant cost, see PR-1 - at exactly one per project. A ConcurrentDictionary
    // because ProjectGraph.resolve evaluates each BFS level's projects in parallel.
    let evaluationCache = ConcurrentDictionary<string, Map<string, string list> * Map<string, string>>()
    let allItemTypes = "ProjectReference" :: FileResolutionIo.fileItemTypes
    let allPropertyNames = FileResolutionIo.filePathPropertyNames

    let evaluateCached (projectPath: string) =
        evaluationCache.GetOrAdd(projectPath, (fun p -> MsBuild.getItemsAndProperties p allItemTypes allPropertyNames))

    let getItemsCached (projectPath: string) : Map<string, string list> = evaluateCached projectPath |> fst
    let getPropertiesCached (projectPath: string) : Map<string, string> = evaluateCached projectPath |> snd

    let projectReferenceResolver: ProjectGraph.ProjectReferenceResolver =
        fun projectPath -> getItemsCached projectPath |> Map.tryFind "ProjectReference" |> Option.defaultValue []

    // Step 1: the transitive project graph.
    let projects = ProjectGraph.resolve projectReferenceResolver projectPath
    let projectDirs = projects |> List.map Path.GetDirectoryName

    // Step 3's solution discovery runs before step 2 because it needs no MSBuild evaluation (just
    // a walk up from the project directory) and step 2 needs its result: the solution root is the
    // ceiling that bounds MSBuild's ancestor-globbed items, .editorconfig above all - see
    // FileResolution.ancestorGlobbedItemTypes.
    let solutionRoot =
        SolutionDiscovery.resolveSolutionRoot
            SolutionDiscoveryIo.solutionFilesOnDisk
            options.SolutionPath
            projectDir

    let ceiling = solutionRoot |> Option.map (fun r -> r.Directory)

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

    // Checked here, before the output-directory partition below, so this failure keeps reporting
    // its own cause (nothing resolved at all) instead of being masked by OutputSafety.validate's
    // "the output directory contains every resolved input file" - which is only true, and only the
    // real problem, once there was something to partition in the first place.
    if List.isEmpty allFiles then
        failwith $"no build-relevant files were resolved for {projectPath}"

    // FR-6: output defaults to ./<ProjectName>, or an explicit -o/--output-dir path.
    let outputDir =
        match options.OutputDir with
        | Some dir -> Path.GetFullPath(dir)
        | None -> Path.GetFullPath(Path.GetFileNameWithoutExtension(projectPath))

    // A previous run's output is indistinguishable from source to MSBuild's implicit globs, so
    // drop anything under the output directory before it can inflate the mirror root or get
    // placed inside itself.
    let partition = OutputSafety.partitionInputs outputDir allFiles

    match OutputSafety.validate outputDir partition with
    | Error message -> failwith message
    | Ok() -> ()

    let allFiles = partition.Kept

    // Step 4: the mirror root - includes the solution root directory itself (see DESIGN.md) so
    // the generated solution file in step 7 always lands inside the output folder.
    let mirrorRootInputDirs =
        (allFiles |> List.map Path.GetDirectoryName)
        @ (solutionRoot |> Option.map (fun r -> [ r.Directory ]) |> Option.defaultValue [])

    let mirrorRoot =
        match MirrorRoot.compute mirrorRootInputDirs with
        | Some root -> root
        | None -> failwith "could not compute a mirror root"

    // Step 5: decide the link strategy once, via a real file already in the resolved set.
    let strategy = LinkStrategy.probe (List.head allFiles) outputDir

    // Step 6: materialize the output folder, capturing pre-existing entries this run didn't
    // produce so the CLI can report them rather than silently deleting or ignoring them.
    let staleEntries = Materialize.materialize strategy options.Clean mirrorRoot outputDir allFiles

    // Step 7: generate the scoped solution file, if a source solution was found.
    match solutionRoot with
    | Some root when root.SolutionFile.EndsWith(".sln") ->
        let sourceContent = File.ReadAllText(root.SolutionFile)
        let filtered = SolutionFile.filterSln root.Directory (Set.ofList projects) sourceContent
        let relativeSlnPath = Path.GetRelativePath(mirrorRoot, root.SolutionFile)
        let outputSlnPath = Path.Combine(outputDir, relativeSlnPath)
        Directory.CreateDirectory(Path.GetDirectoryName(outputSlnPath)) |> ignore
        SolutionFileIo.write outputSlnPath filtered
    | Some root when root.SolutionFile.EndsWith(".slnx") ->
        let sourceContent = File.ReadAllText(root.SolutionFile)
        let filtered = SolutionFileXml.filterSlnx root.Directory (Set.ofList projects) sourceContent
        let relativeSlnPath = Path.GetRelativePath(mirrorRoot, root.SolutionFile)
        let outputSlnPath = Path.Combine(outputDir, relativeSlnPath)
        Directory.CreateDirectory(Path.GetDirectoryName(outputSlnPath)) |> ignore
        SolutionFileXmlIo.write outputSlnPath filtered
    | Some root ->
        // Solution discovery only ever finds .sln/.slnx, so this is unreachable in practice.
        ignore root
    | None -> ()

    { OutputDir = outputDir
      IncludedProjects = projects
      FileCount = allFiles.Length
      SolutionRoot = solutionRoot
      Strategy = strategy
      ExcludedUnderOutput = partition.ExcludedUnderOutput
      StaleEntries = staleEntries
      MissingFiles = resolvedProjectFiles.MissingItemFiles }
