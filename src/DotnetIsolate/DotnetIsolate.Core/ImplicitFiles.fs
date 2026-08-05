module DotnetIsolate.Core.ImplicitFiles

open System.IO

/// Returns which well-known files exist directly inside `directory`.
type FilesInDirectory = string -> string list

/// Walks up from `startDir` (inclusive) to `ceiling` (inclusive), collecting every well-known
/// file found along the way - not just the nearest, since Directory.Build.props chains commonly
/// import further-up parents explicitly (FR-5). `ceiling = None` walks all the way to the
/// filesystem root, for when no solution file was found (FR-8).
let resolve (filesIn: FilesInDirectory) (ceiling: string option) (startDir: string) : string list =
    let rec go (dir: string option) : string list =
        match dir with
        | None -> []
        | Some dir ->
            let here = filesIn dir

            if Some dir = ceiling then
                here
            else
                here @ go (Path.GetDirectoryName(dir) |> Option.ofObj)

    go (Some startDir)

/// Resolves and deduplicates well-known files across every directory in `projectDirs`.
let resolveForProjects (filesIn: FilesInDirectory) (ceiling: string option) (projectDirs: string list) : string list =
    projectDirs |> List.collect (resolve filesIn ceiling) |> List.distinct
