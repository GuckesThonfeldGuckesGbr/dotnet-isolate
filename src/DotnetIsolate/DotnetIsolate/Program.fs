module Program

open Argu
open DotnetIsolate.Core

type Arguments =
    | [<MainCommand; ExactlyOnce>] Project_Paths of paths: string list
    | [<AltCommandLine("-o")>] Output_Dir of dir: string
    | [<AltCommandLine("-s")>] Solution of path: string
    | Clean

    interface IArgParserTemplate with
        member this.Usage =
            match this with
            | Project_Paths _ -> "one or more .csproj/.fsproj files to isolate"
            | Output_Dir _ -> "output directory (default: ./<ProjectName>)"
            | Solution _ -> "solution file to use, overriding auto-discovery (FR-8)"
            | Clean -> "delete and recreate the output directory instead of merging into it"

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
                  RestoreOnly = false
                  Clean = results.Contains(Clean) }

        // FR-8: always report which solution was used, since more than one solution can
        // reference the same project and the choice isn't always obvious.
        match result.SolutionRoot with
        | Some root -> printfn $"Using solution ({describeSolutionSource root.Source}): {root.SolutionFile}"
        | None -> printfn "No solution file found; skipping solution generation."

        printfn
            $"Isolated {result.IncludedProjects.Length} project(s), {result.FileCount} file(s) into {result.OutputDir}"

        printfn $"Link strategy: {result.Strategy}"

        for entry in result.ExcludedUnderOutput do
            eprintfn $"warning: ignoring {entry}, which lives under the output directory"

        for entry in result.StaleEntries do
            eprintfn $"warning: {entry} was already in the output directory and was not produced by this run"

        for entry in result.MissingFiles do
            eprintfn $"warning: {entry} is referenced by a project but does not exist; skipped"

        0
    with
    | :? ArguParseException as ex ->
        eprintfn $"{ex.Message}"
        1
    | ex ->
        eprintfn $"Error: {ex.Message}"
        1
