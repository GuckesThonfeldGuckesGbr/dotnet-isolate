module DotnetIsolate.Core.LinkStrategy

open System.IO

type Strategy =
    | Hardlink
    | Copy

/// Determines whether hardlinking works from `sampleSourceFile`'s volume/filesystem into
/// `destinationRoot`, via a single upfront probe: hardlink that one real, already-existing file
/// to a throwaway name under `destinationRoot`, and use the result - not per-file exception
/// handling (REL-2). Deliberately never touches the source tree: the probe target is a file we're
/// already going to copy, not a scratch file written into the user's original solution, and the
/// throwaway destination copy is deleted afterward regardless of outcome.
let probe (sampleSourceFile: string) (destinationRoot: string) : Strategy =
    Directory.CreateDirectory(destinationRoot) |> ignore

    let probeDestination =
        Path.Combine(destinationRoot, $".dotnet-isolate-probe-{Path.GetRandomFileName()}")

    try
        match Hardlink.create sampleSourceFile probeDestination with
        | Ok () -> Hardlink
        | Error _ -> Copy
    finally
        if File.Exists(probeDestination) then
            File.Delete(probeDestination)
