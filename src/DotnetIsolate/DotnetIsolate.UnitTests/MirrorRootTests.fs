module DotnetIsolate.UnitTests.MirrorRootTests

open Xunit
open DotnetIsolate.Core.MirrorRoot

[<Fact>]
let ``a single directory is its own mirror root`` () =
    Assert.Equal(Some "/repo/src/A", compute [ "/repo/src/A" ])

[<Fact>]
let ``the common ancestor of sibling directories is their shared parent`` () =
    Assert.Equal(Some "/repo/src", compute [ "/repo/src/A"; "/repo/src/B" ])

[<Fact>]
let ``diverges on the full segment, not a raw string prefix`` () =
    // "/repo/src2" must not be mistaken for a descendant of "/repo/src".
    Assert.Equal(Some "/repo", compute [ "/repo/src"; "/repo/src2" ])

[<Fact>]
let ``a directory nested inside another still resolves to the shallower one`` () =
    Assert.Equal(Some "/repo", compute [ "/repo"; "/repo/src/A" ])

[<Fact>]
let ``returns none for an empty input`` () = Assert.Equal(None, compute [])

[<Fact>]
let ``a repeated directory does not change the result`` () =
    Assert.Equal(Some "/repo/src/A", compute [ "/repo/src/A"; "/repo/src/A" ])
