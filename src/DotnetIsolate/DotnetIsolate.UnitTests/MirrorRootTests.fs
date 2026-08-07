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
let ``directories that differ only in case are treated as the same ancestor`` () =
    Assert.Equal(Some(path [ "Repo"; "src" ]), compute [ path [ "Repo"; "src" ]; path [ "repo"; "src" ] ])

[<Fact>]
let ``a repeated directory does not change the result`` () =
    Assert.Equal(
        Some(path [ "repo"; "src"; "A" ]),
        compute [ path [ "repo"; "src"; "A" ]; path [ "repo"; "src"; "A" ] ]
    )

[<Fact>]
let ``isUnder accepts the directory itself and anything beneath it`` () =
    let root = path [ "repo" ]

    Assert.True(isUnder root root)
    Assert.True(isUnder root (path [ "repo"; ".editorconfig" ]))
    Assert.True(isUnder root (path [ "repo"; "src"; "A"; "A.fsproj" ]))

[<Fact>]
let ``isUnder rejects ancestors, siblings, and merely-prefixed neighbours`` () =
    let root = path [ "repo"; "src" ]

    Assert.False(isUnder root (path [ "repo"; ".editorconfig" ]))
    Assert.False(isUnder root (path [ "elsewhere"; "x" ]))
    // "src2" shares a string prefix with "src" but is not below it.
    Assert.False(isUnder root (path [ "repo"; "src2"; "x" ]))
