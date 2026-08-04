module DotnetIsolate.Core.Materialize

open System.IO

/// Places `sourceFile` (an absolute path under `mirrorRoot`) at its mirrored relative location
/// under `outputRoot`, using `strategy` - decided once, upfront, by LinkStrategy.probe (REL-2).
/// Creates intermediate directories as needed. A hardlink failure here (the strategy said
/// Hardlink, but this specific file couldn't be linked) is a genuine anomaly worth surfacing, not
/// something to silently paper over per file - REL-2 is explicit that the decision is made once,
/// not re-litigated file by file.
let placeFile (strategy: LinkStrategy.Strategy) (mirrorRoot: string) (outputRoot: string) (sourceFile: string) =
    let relative = Path.GetRelativePath(mirrorRoot, sourceFile)
    let destination = Path.Combine(outputRoot, relative)
    Directory.CreateDirectory(Path.GetDirectoryName(destination)) |> ignore

    match strategy with
    | LinkStrategy.Hardlink ->
        match Hardlink.create sourceFile destination with
        | Ok () -> ()
        | Error code ->
            failwith
                $"Hardlink.create failed for {sourceFile} -> {destination} (error {code}) despite the strategy probe succeeding"
    | LinkStrategy.Copy -> File.Copy(sourceFile, destination, overwrite = true)

/// Deletes `outputRoot` if it already exists and recreates it empty (FR-7), then places every
/// file in `files` (absolute paths under `mirrorRoot`) at its mirrored relative location using
/// `strategy` (pipeline step 6).
let materialize (strategy: LinkStrategy.Strategy) (mirrorRoot: string) (outputRoot: string) (files: string list) =
    if Directory.Exists(outputRoot) then
        Directory.Delete(outputRoot, recursive = true)

    Directory.CreateDirectory(outputRoot) |> ignore
    files |> List.iter (placeFile strategy mirrorRoot outputRoot)
