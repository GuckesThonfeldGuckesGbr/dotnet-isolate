module DotnetIsolate.UnitTests.OutputSafetyTests

open Xunit
open DotnetIsolate.Core
open DotnetIsolate.UnitTests.PathHelpers

[<Fact>]
let ``partitionInputs keeps files outside the output directory`` () =
    let outputDir = path [ "repo"; "out" ]
    let a = path [ "repo"; "src"; "A"; "A.fsproj" ]
    let b = path [ "repo"; "src"; "B"; "B.fsproj" ]

    let result = OutputSafety.partitionInputs outputDir [ a; b ]

    Assert.Equal<string list>([ a; b ], result.Kept)
    Assert.Empty(result.ExcludedUnderOutput)

[<Fact>]
let ``partitionInputs excludes files under the output directory`` () =
    let outputDir = path [ "repo"; "src"; "A"; "out" ]
    let source = path [ "repo"; "src"; "A"; "A.fsproj" ]
    let stale = path [ "repo"; "src"; "A"; "out"; "src"; "B"; "B.fsproj" ]

    let result = OutputSafety.partitionInputs outputDir [ source; stale ]

    Assert.Equal<string list>([ source ], result.Kept)
    Assert.Equal<string list>([ stale ], result.ExcludedUnderOutput)

// The output directory itself is "under" itself, so a file sitting directly in it is excluded.
[<Fact>]
let ``partitionInputs excludes a file directly inside the output directory`` () =
    let outputDir = path [ "repo"; "out" ]
    let inside = path [ "repo"; "out"; "A.fsproj" ]

    let result = OutputSafety.partitionInputs outputDir [ inside ]

    Assert.Empty(result.Kept)
    Assert.Equal<string list>([ inside ], result.ExcludedUnderOutput)

// Segment-wise comparison: "out2" must not be mistaken for a child of "out".
[<Fact>]
let ``partitionInputs does not treat a prefix-sharing sibling as inside the output directory`` () =
    let outputDir = path [ "repo"; "out" ]
    let sibling = path [ "repo"; "out2"; "A.fsproj" ]

    let result = OutputSafety.partitionInputs outputDir [ sibling ]

    Assert.Equal<string list>([ sibling ], result.Kept)
    Assert.Empty(result.ExcludedUnderOutput)

[<Fact>]
let ``partitionInputs matches case-insensitively`` () =
    let outputDir = path [ "repo"; "Out" ]
    let inside = path [ "repo"; "out"; "A.fsproj" ]

    let result = OutputSafety.partitionInputs outputDir [ inside ]

    Assert.Equal<string list>([ inside ], result.ExcludedUnderOutput)

// The trailing-separator reproduction, and the reason MirrorRoot.isUnder trims before comparing.
// Shell tab-completion appends a separator to a directory argument routinely, and
// Path.GetFullPath preserves it, so "-o out/" reached here as ".../out/" - which splits into a
// trailing *empty* segment and therefore matched nothing at all. Every output-directory safeguard
// silently switched off: nothing was excluded, so `Kept` stayed non-empty, so `validate` saw no
// self-consumption, so `--clean` went on to delete the directory. That is the data-loss bug this
// branch exists to fix, re-armed by one character.
[<Fact>]
let ``partitionInputs excludes files under an output directory given with a trailing separator`` () =
    let outputDir = path [ "repo"; "out" ] + string System.IO.Path.DirectorySeparatorChar
    let inside = path [ "repo"; "out"; "A.fsproj" ]
    let outside = path [ "repo"; "src"; "A"; "A.fsproj" ]

    let result = OutputSafety.partitionInputs outputDir [ inside; outside ]

    Assert.Equal<string list>([ outside ], result.Kept)
    Assert.Equal<string list>([ inside ], result.ExcludedUnderOutput)

[<Fact>]
let ``validate succeeds when files survive the partition`` () =
    let outputDir = path [ "repo"; "out" ]
    let partition =
        { OutputSafety.Kept = [ path [ "repo"; "src"; "A"; "A.fsproj" ] ]
          OutputSafety.ExcludedUnderOutput = [] }

    Assert.Equal(Ok(), OutputSafety.validate false outputDir partition)

[<Fact>]
let ``validate fails when the output directory consumed every input`` () =
    let outputDir = path [ "repo" ]
    let partition =
        { OutputSafety.Kept = []
          OutputSafety.ExcludedUnderOutput = [ path [ "repo"; "src"; "A"; "A.fsproj" ] ] }

    match OutputSafety.validate false outputDir partition with
    | Ok() -> failwith "expected validation to fail"
    | Error message ->
        Assert.Contains("output directory", message)
        // The message must name the offending directory so the user can act on it.
        Assert.Contains(outputDir, message)

// Partial overlap is survivable when merging - the excluded inputs are only dropped, and the user
// is warned - but --clean *deletes* the output directory first, so an input living inside it is
// destroyed. Merge tolerates overlap; clean must demand disjointness.
[<Fact>]
let ``validate fails with clean when the output directory contains any input`` () =
    let outputDir = path [ "repo"; "src"; "Shared" ]
    let doomed = path [ "repo"; "src"; "Shared"; "Shared.fsproj" ]
    let partition =
        { OutputSafety.Kept = [ path [ "repo"; "src"; "A"; "A.fsproj" ] ]
          OutputSafety.ExcludedUnderOutput = [ doomed ] }

    match OutputSafety.validate true outputDir partition with
    | Ok() -> failwith "expected validation to fail"
    | Error message ->
        // Both the directory that would be deleted and something it would take with it.
        Assert.Contains(outputDir, message)
        Assert.Contains(doomed, message)

[<Fact>]
let ``validate succeeds with clean when the output directory contains no input`` () =
    let outputDir = path [ "repo"; "out" ]
    let partition =
        { OutputSafety.Kept = [ path [ "repo"; "src"; "A"; "A.fsproj" ] ]
          OutputSafety.ExcludedUnderOutput = [] }

    Assert.Equal(Ok(), OutputSafety.validate true outputDir partition)
