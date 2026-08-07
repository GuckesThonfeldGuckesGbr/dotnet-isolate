module DotnetIsolate.UnitTests.PathHelpersTests

open System.IO
open Xunit
open DotnetIsolate.UnitTests.PathHelpers

/// A tripwire for the test helper itself. Rooted is not the same as fully qualified on Windows,
/// and several Core APIs (Path.GetFullPath with a basePath, above all) reject the former - so a
/// helper that quietly produced "\repo\src" would fail the suite deep inside unrelated stack
/// traces rather than here. Invisible on Unix, where the two notions coincide.
[<Fact>]
let ``path produces a fully qualified path, not merely a rooted one`` () =
    let result = path [ "repo"; "src"; "A" ]

    Assert.True(Path.IsPathFullyQualified(result), $"not fully qualified: {result}")
    Assert.True(Path.IsPathRooted(result), $"not rooted: {result}")

/// The walk-up in ImplicitFiles and the segment splitting in MirrorRoot both depend on this.
[<Fact>]
let ``path round-trips through GetDirectoryName down to the root`` () =
    let deepest = path [ "repo"; "src"; "A" ]

    Assert.Equal(path [ "repo"; "src" ], Path.GetDirectoryName(deepest))
    Assert.Equal(path [ "repo" ], Path.GetDirectoryName(path [ "repo"; "src" ]))
