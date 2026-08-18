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

/// Segments for *comparison*, with trailing empty ones dropped.
///
/// Splitting "/repo/out/" yields a trailing empty segment that no real path segment can ever
/// equal, so isUnder matched nothing at all for any directory carrying a trailing separator. That
/// was not cosmetic: `-o out/` (which is what shell tab-completion produces, and which
/// Path.GetFullPath faithfully preserves) disabled every output-directory safeguard at once -
/// nothing was excluded as living under the output, so the self-consumption check saw a non-empty
/// kept set and passed, and `--clean` then ran Directory.Delete over the source tree. The
/// pipeline also trims the output path once, at the point it is computed; this is the same
/// defence made intrinsic to the primitive, for every caller (the file-resolution ceiling too),
/// since the next caller will not know to trim.
///
/// Deliberately *not* folded into `segments` itself: `compute` reconstructs a path by joining its
/// segments, and on Unix the leading empty segment of "/repo" is what makes that join come back
/// out rooted ("/repo" rather than "repo"). Only trailing empties are dropped, and only here.
let private comparisonSegments (dir: string) : string list =
    segments dir |> List.rev |> List.skipWhile System.String.IsNullOrEmpty |> List.rev

/// True when `path` is `directory` itself or sits somewhere beneath it - segment-wise, so
/// "/repo/src2/x" is never mistaken for a descendant of "/repo/src". Both must be absolute.
/// A trailing separator on either argument is insignificant, as it is to the filesystem.
let isUnder (directory: string) (path: string) : bool =
    let dirSegments = comparisonSegments directory

    commonPrefixOfTwo dirSegments (comparisonSegments path) |> List.length = List.length dirSegments

/// True when two paths name the same directory - segment-wise and case-insensitively, with a
/// trailing separator on either side insignificant, exactly as `isUnder` treats them.
///
/// Kept here rather than at the call site because path comparison is this module's job: raw string
/// equality gets it wrong on Windows and macOS (`C:\Repo\App` vs `C:\repo\App`) and wrong anywhere
/// for `App` vs `App/`, and every caller would have to rediscover that.
let sameDirectory (a: string) (b: string) : bool =
    let aSegments = comparisonSegments a
    let bSegments = comparisonSegments b

    List.length aSegments = List.length bSegments
    && commonPrefixOfTwo aSegments bSegments |> List.length = List.length aSegments

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
