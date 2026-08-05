module DotnetIsolate.UnitTests.MirrorRootTests

open Xunit
open DotnetIsolate.Core.MirrorRoot
open DotnetIsolate.UnitTests.PathHelpers

[<Fact>]
let ``a single directory is its own mirror root`` () =
    Assert.Equal(Some(path [ "repo"; "src"; "A" ]), compute [ path [ "repo"; "src"; "A" ] ])

[<Fact>]
let ``the common ancestor of sibling directories is their shared parent`` () =
    Assert.Equal(
        Some(path [ "repo"; "src" ]),
        compute [ path [ "repo"; "src"; "A" ]; path [ "repo"; "src"; "B" ] ]
    )

[<Fact>]
let ``diverges on the full segment, not a raw string prefix`` () =
    // ".../src2" must not be mistaken for a descendant of ".../src".
    Assert.Equal(Some(path [ "repo" ]), compute [ path [ "repo"; "src" ]; path [ "repo"; "src2" ] ])

[<Fact>]
let ``a directory nested inside another still resolves to the shallower one`` () =
    Assert.Equal(Some(path [ "repo" ]), compute [ path [ "repo" ]; path [ "repo"; "src"; "A" ] ])

[<Fact>]
let ``returns none for an empty input`` () = Assert.Equal(None, compute [])

[<Fact>]
let ``a repeated directory does not change the result`` () =
    Assert.Equal(
        Some(path [ "repo"; "src"; "A" ]),
        compute [ path [ "repo"; "src"; "A" ]; path [ "repo"; "src"; "A" ] ]
    )
