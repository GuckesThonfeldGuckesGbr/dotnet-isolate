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

// A trailing separator used to make isUnder match *nothing*: the split produced a trailing empty
// segment that no real path segment could ever equal. Since callers get their directory from
// Path.GetFullPath - which preserves whatever the user typed, and shells tab-complete a trailing
// separator onto directories - that turned off the output-directory exclusion entirely and, with
// --clean, put the source tree back in reach of Directory.Delete.
[<Fact>]
let ``isUnder ignores a trailing separator on either argument`` () =
    let separator = string System.IO.Path.DirectorySeparatorChar
    let root = path [ "repo"; "src" ]

    Assert.True(isUnder (root + separator) (path [ "repo"; "src"; "A"; "A.fsproj" ]))
    Assert.True(isUnder (root + separator) root)
    Assert.True(isUnder root (path [ "repo"; "src"; "A" ] + separator))
    // Still segment-wise: a trailing separator must not make a neighbour match either.
    Assert.False(isUnder (root + separator) (path [ "repo"; "src2"; "x" ]))
