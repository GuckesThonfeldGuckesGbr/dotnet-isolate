module DotnetIsolate.Core.Pipeline

open System.Collections.Concurrent
open System.IO

type IsolateOptions =
    { ProjectPath: string
      OutputDir: string option
      SolutionPath: string option }

type IsolateResult =
    { OutputDir: string
      IncludedProjects: string list
      FileCount: int
      SolutionRoot: SolutionDiscovery.SolutionRoot option
      Strategy: LinkStrategy.Strategy }

/// Runs the full pipeline described in DESIGN.md (steps 1-7) end to end: resolves the project
/// graph and its files, locates the solution (FR-8) and implicit repo-level files, computes the
/// mirror root, decides the link strategy, materializes the output folder, and - when a solution
/// was found - generates a scoped copy of it. Returns enough to report what happened; console
/// output (e.g. which solution was used, per FR-8) is the CLI's job, not this function's.
let isolate (options: IsolateOptions) : IsolateResult =
    let projectPath = Path.GetFullPath(options.ProjectPath)
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

    let projectFiles = FileResolution.resolveAllFiles resolvers projects

    // Step 3 (continued): the implicit repo-level files, up to the same ceiling.
    let implicitFiles =
        ImplicitFiles.resolveForProjects ImplicitFilesIo.filesOnDisk ceiling projectDirs

    let allFiles = (projectFiles @ implicitFiles) |> List.distinct

    if List.isEmpty allFiles then
        failwith $"no build-relevant files were resolved for {projectPath}"

    // Step 4: the mirror root - includes the solution root directory itself (see DESIGN.md) so
    // the generated solution file in step 7 always lands inside the output folder.
    let mirrorRootInputDirs =
        (allFiles |> List.map Path.GetDirectoryName)
        @ (solutionRoot |> Option.map (fun r -> [ r.Directory ]) |> Option.defaultValue [])

    let mirrorRoot =
        match MirrorRoot.compute mirrorRootInputDirs with
        | Some root -> root
        | None -> failwith "could not compute a mirror root"

    // FR-6: output defaults to ./<ProjectName>, or an explicit -o/--output-dir path.
    let outputDir =
        match options.OutputDir with
        | Some dir -> Path.GetFullPath(dir)
        | None -> Path.GetFullPath(Path.GetFileNameWithoutExtension(projectPath))

    // Step 5: decide the link strategy once, via a real file already in the resolved set.
    let strategy = LinkStrategy.probe (List.head allFiles) outputDir

    // Step 6: materialize the output folder. `clean = false` for now (Task 3 wires the stale-entry
    // list this returns into the pipeline result and decides `clean` properly).
    Materialize.materialize strategy false mirrorRoot outputDir allFiles |> ignore

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
      Strategy = strategy }
