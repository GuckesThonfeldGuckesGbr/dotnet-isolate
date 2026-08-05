module DotnetIsolate.Core.MirrorRoot

open System.IO

let private segments (dir: string) : string list =
    dir.Split([| Path.DirectorySeparatorChar; Path.AltDirectorySeparatorChar |]) |> Array.toList

let private commonPrefixOfTwo (a: string list) (b: string list) : string list =
    let rec go a b =
        match a, b with
        | x :: xs, y :: ys when System.String.Equals(x, y, System.StringComparison.OrdinalIgnoreCase) -> x :: go xs ys
        | _ -> []

    go a b

/// Computes the longest common ancestor directory shared by every directory in `directories`
/// (FR-2, pipeline step 4) - compares path segments, not raw string prefixes, so
/// "/repo/src2" is never mistaken for a descendant of "/repo/src". None for an empty input.
///
/// Expects *directories*, not file paths - callers resolving a set of files should pass
/// `Path.GetDirectoryName` of each first, otherwise a lone input file's name would be treated as
/// part of the root. Not meaningful across filesystem roots that share no ancestor at all (e.g.
/// different Windows drive letters) - solutions spanning drives/volumes are out of scope, same
/// assumption DESIGN.md's link-strategy probe already makes.
let compute (directories: string list) : string option =
    match directories with
    | [] -> None
    | first :: rest ->
        let commonSegments =
            rest |> List.fold (fun acc dir -> commonPrefixOfTwo acc (segments dir)) (segments first)

        Some(String.concat (string Path.DirectorySeparatorChar) commonSegments)
