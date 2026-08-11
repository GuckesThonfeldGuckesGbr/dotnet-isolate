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
      MissingFiles: string list
      /// Entry projects that do not live under the discovered solution's directory. Solution
      /// discovery only ever walks up from the first entry, so these were never checked against
      /// it: the generated scoped solution file may omit them even though their files were
      /// isolated, and any ancestor-globbed ceiling files (e.g. .editorconfig) above their own
      /// directory tree were never picked up either. Empty when no solution was found.
      EntriesOutsideDiscoveredSolution: string list }

/// Runs the full pipeline described in DESIGN.md (steps 1-7) end to end: resolves the project
/// graph and its files, locates the solution (FR-8) and implicit repo-level files, computes the
/// mirror root, decides the link strategy, materializes the output folder, and - when a solution
/// was found - generates a scoped copy of it. Returns enough to report what happened; console
/// output (e.g. which solution was used, per FR-8) is the CLI's job, not this function's.
let isolate (options: IsolateOptions) : IsolateResult =
    let projectPaths = options.ProjectPaths |> List.map Path.GetFullPath

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
    let allPropertyNames = FileResolutionIo.filePathPropertyNames

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
            options.SolutionPath
            projectDir

    let ceiling = solutionRoot |> Option.map (fun r -> r.Directory)

    // The consequence of the limitation noted above, made visible: an entry outside the
    // discovered solution's directory tree got its files isolated (steps 1-2 don't care where a
    // solution is), but step 7 below filters the *source* solution's contents, so it can only ever
    // include projects that were already members of it - and ancestor-globbed ceiling files above
    // this entry's own tree were never resolved either, since `ceiling` only bounds the discovered
    // solution's directory.
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

    // Checked here, before the output-directory partition below, so this failure keeps reporting
    // its own cause (nothing resolved at all) instead of being masked by OutputSafety.validate's
    // "the output directory contains every resolved input file" - which is only true, and only the
    // real problem, once there was something to partition in the first place.
    if List.isEmpty allFiles then
        let describedProjectPaths = String.concat ", " projectPaths
        failwith $"no build-relevant files were resolved for {describedProjectPaths}"

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
    let partition = OutputSafety.partitionInputs outputDir allFiles

    match OutputSafety.validate options.Clean outputDir partition with
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

    // The restore phase is applied after the mirror root is computed from the full set, so both
    // phases share one root and their outputs overlay exactly - which is what lets a Dockerfile
    // COPY the restore half, run `dotnet restore`, then COPY the full half over it.
    let filesToPlace =
        if options.RestoreOnly then
            Phase.restoreSubset projects allFiles
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
      FileCount = filesToPlace.Length
      SolutionRoot = solutionRoot
      Strategy = strategy
      ExcludedUnderOutput = partition.ExcludedUnderOutput
      StaleEntries = staleEntries
      MissingFiles = resolvedProjectFiles.MissingItemFiles
      EntriesOutsideDiscoveredSolution = entriesOutsideDiscoveredSolution }
