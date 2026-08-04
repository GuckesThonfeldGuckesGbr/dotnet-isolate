module DotnetIsolate.Core.Pipeline

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

    // Step 1: the transitive project graph.
    let projects = ProjectGraph.resolve MsBuild.projectReferenceResolver projectPath
    let projectDirs = projects |> List.map Path.GetDirectoryName

    // Step 2: each project's build-relevant files.
    let projectFiles =
        FileResolution.resolveAllFiles FileResolution.projectItemsResolver projects

    // Step 3: locate the solution, then resolve implicit repo-level files up to it.
    let solutionRoot =
        SolutionDiscovery.resolveSolutionRoot SolutionDiscovery.solutionFilesOnDisk options.SolutionPath projectDir

    let ceiling = solutionRoot |> Option.map (fun r -> r.Directory)

    let implicitFiles =
        ImplicitFiles.resolveForProjects ImplicitFiles.filesOnDisk ceiling projectDirs

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

    // Step 6: materialize the output folder (deletes/recreates per FR-7).
    Materialize.materialize strategy mirrorRoot outputDir allFiles

    // Step 7: generate the scoped solution file, if a source solution was found.
    match solutionRoot with
    | Some root when root.SolutionFile.EndsWith(".sln") ->
        let sourceContent = File.ReadAllText(root.SolutionFile)
        let filtered = SolutionFile.filterSln root.Directory (Set.ofList projects) sourceContent
        let relativeSlnPath = Path.GetRelativePath(mirrorRoot, root.SolutionFile)
        let outputSlnPath = Path.Combine(outputDir, relativeSlnPath)
        Directory.CreateDirectory(Path.GetDirectoryName(outputSlnPath)) |> ignore
        SolutionFile.write outputSlnPath filtered
    | Some root ->
        // .slnx source - filtering not implemented yet (see DESIGN.md), so nothing is generated.
        ignore root
    | None -> ()

    { OutputDir = outputDir
      IncludedProjects = projects
      FileCount = allFiles.Length
      SolutionRoot = solutionRoot
      Strategy = strategy }
