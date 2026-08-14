module Program

open Argu
open DotnetIsolate.Core

type Arguments =
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

let private describeSolutionSource (source: SolutionDiscovery.SolutionRootSource) =
    match source with
    | SolutionDiscovery.ExplicitlyProvided -> "explicitly provided"
    | SolutionDiscovery.AutoDiscovered -> "auto-discovered"

[<EntryPoint>]
let main argv =
    let parser = ArgumentParser.Create<Arguments>(programName = "dotnet-isolate")

    try
        let results = parser.ParseCommandLine(argv)

        let result =
            Pipeline.isolate
                { ProjectPaths = results.GetResult(Project_Paths)
                  OutputDir = results.TryGetResult(Output_Dir)
                  SolutionPath = results.TryGetResult(Solution)
                  RestoreOnly = results.Contains(Restore)
                  Clean = results.Contains(Clean) }

        // FR-8: always report which solution was used, since more than one solution can
        // reference the same project and the choice isn't always obvious.
        match result.SolutionRoot with
        | Some root -> printfn $"Using solution ({describeSolutionSource root.Source}): {root.SolutionFile}"
        | None -> printfn "No solution file found; skipping solution generation."

        printfn
            $"Isolated {result.IncludedProjects.Length} project(s), {result.FileCount} file(s) into {result.OutputDir}"

        printfn $"Link strategy: {result.Strategy}"

        for warning in Report.warnings result do
            eprintfn $"{warning}"

        0
    with
    | :? ArguParseException as ex ->
        eprintfn $"{ex.Message}"
        1
    | ex ->
        eprintfn $"Error: {ex.Message}"
        1
