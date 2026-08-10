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
        // Unlike File.Copy, creating a hardlink at a path that already has an entry fails
        // (EEXIST) rather than replacing it. Now that merging into an existing output directory
        // is the default (Task 3), re-running over the same output hits this on every file the
        // previous run already placed, so the existing entry has to be removed first.
        if File.Exists(destination) then
            File.Delete(destination)

        match Hardlink.create sourceFile destination with
        | Ok () -> ()
        | Error code ->
            failwith
                $"Hardlink.create failed for {sourceFile} -> {destination} (error {code}) despite the strategy probe succeeding"
    | LinkStrategy.Copy -> File.Copy(sourceFile, destination, overwrite = true)

/// Places every file in `files` (absolute paths under `mirrorRoot`) at its mirrored relative
/// location under `outputRoot`, using `strategy` (pipeline step 6).
///
/// `clean` selects between the two output-directory policies. The default (`false`) merges into
/// whatever is already there and returns the entries it found that this run did not produce, so
/// the caller can warn about them; it never deletes. `true` restores the original
/// delete-and-recreate behaviour for callers who need the output to contain exactly the isolated
/// set. Merging is the default because the destructive one, applied to an output directory that
/// overlapped the source tree, deleted the user's source before failing.
let materialize
    (strategy: LinkStrategy.Strategy)
    (clean: bool)
    (mirrorRoot: string)
    (outputRoot: string)
    (files: string list)
    : string list =
    if clean && Directory.Exists(outputRoot) then
        Directory.Delete(outputRoot, recursive = true)

    // Snapshot before placing anything, so files this run writes are never mistaken for stale.
    let preExisting =
        if Directory.Exists(outputRoot) then
            Directory.EnumerateFiles(outputRoot, "*", SearchOption.AllDirectories) |> Set.ofSeq
        else
            Set.empty

    Directory.CreateDirectory(outputRoot) |> ignore
    files |> List.iter (placeFile strategy mirrorRoot outputRoot)

    let produced =
        files
        |> List.map (fun f -> Path.Combine(outputRoot, Path.GetRelativePath(mirrorRoot, f)))
        |> Set.ofList

    Set.difference preExisting produced |> Set.toList
