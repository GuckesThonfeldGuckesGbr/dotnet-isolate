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
