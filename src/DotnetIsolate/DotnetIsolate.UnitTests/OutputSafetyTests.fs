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

[<Fact>]
let ``validate succeeds when files survive the partition`` () =
    let outputDir = path [ "repo"; "out" ]
    let partition =
        { OutputSafety.Kept = [ path [ "repo"; "src"; "A"; "A.fsproj" ] ]
          OutputSafety.ExcludedUnderOutput = [] }

    Assert.Equal(Ok(), OutputSafety.validate outputDir partition)

[<Fact>]
let ``validate fails when the output directory consumed every input`` () =
    let outputDir = path [ "repo" ]
    let partition =
        { OutputSafety.Kept = []
          OutputSafety.ExcludedUnderOutput = [ path [ "repo"; "src"; "A"; "A.fsproj" ] ] }

    match OutputSafety.validate outputDir partition with
    | Ok() -> failwith "expected validation to fail"
    | Error message ->
        Assert.Contains("output directory", message)
        // The message must name the offending directory so the user can act on it.
        Assert.Contains(outputDir, message)
