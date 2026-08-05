module DotnetIsolate.Core.SolutionDiscovery

open System.IO

/// Returns the solution files (.sln/.slnx) found directly inside `directory`.
type SolutionFilesInDirectory = string -> string list

/// How the solution root ended up being what it is - worth surfacing to the user (FR-8), since
/// more than one solution can reference the same project.
type SolutionRootSource =
    | ExplicitlyProvided
    | AutoDiscovered

type SolutionRoot =
    { Directory: string
      SolutionFile: string
      Source: SolutionRootSource }

/// Walks up from `startDir` (inclusive) to the filesystem root, returning the first ancestor
/// directory that contains a solution file, and that file's path. If more than one solution file
/// is found in the same directory, the alphabetically-first is used, for a deterministic result
/// (REL-1). None if no solution file is found before the filesystem root.
let private findSolutionRoot (filesIn: SolutionFilesInDirectory) (startDir: string) : (string * string) option =
    let rec go (dir: string option) =
        match dir with
        | None -> None
        | Some dir ->
            match filesIn dir |> List.sort with
            | solutionFile :: _ -> Some(dir, solutionFile)
            | [] -> go (Path.GetDirectoryName(dir) |> Option.ofObj)

    go (Some startDir)

/// Resolves the solution root to use (FR-8): an explicitly-provided solution path always wins;
/// otherwise auto-discover by walking up from `startProjectDir`. None only when nothing was
/// provided and auto-discovery found no solution before the filesystem root.
let resolveSolutionRoot
    (filesIn: SolutionFilesInDirectory)
    (explicitSolutionPath: string option)
    (startProjectDir: string)
    : SolutionRoot option =
    match explicitSolutionPath with
    | Some path ->
        Some
            { Directory = Path.GetDirectoryName(path)
              SolutionFile = path
              Source = ExplicitlyProvided }
    | None ->
        findSolutionRoot filesIn startProjectDir
        |> Option.map (fun (dir, file) ->
            { Directory = dir
              SolutionFile = file
              Source = AutoDiscovered })
