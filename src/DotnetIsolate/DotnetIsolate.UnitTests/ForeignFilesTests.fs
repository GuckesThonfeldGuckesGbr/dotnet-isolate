module DotnetIsolate.UnitTests.ForeignFilesTests

open Xunit
open DotnetIsolate.Core
open DotnetIsolate.UnitTests.PathHelpers

/// A ContainsProjectFile that says yes for exactly the directories given - the same shape
/// BuildArtifactsTests uses, since both rules anchor on the identical probe.
let private projectsIn (dirs: string list) : BuildArtifacts.ContainsProjectFile =
    let set = Set.ofList dirs
    fun dir -> Set.contains dir set

[<Fact>]
let ``the owning directory of a file is the project directory it sits in`` () =
    let app = path [ "repo"; "App" ]
    let file = path [ "repo"; "App"; "Program.cs" ]

    Assert.Equal(Some app, ForeignFiles.owningProjectDirectory (projectsIn [ app ]) file)

[<Fact>]
let ``the owning directory is the nearest project ancestor, not the outermost`` () =
    let outer = path [ "repo" ]
    let app = path [ "repo"; "App" ]
    let file = path [ "repo"; "App"; "Models"; "Order.cs" ]

    Assert.Equal(Some app, ForeignFiles.owningProjectDirectory (projectsIn [ outer; app ]) file)

// The deliberate cross-project link the rule must stay quiet about: no project owns the directory.
[<Fact>]
let ``a file no project directory contains has no owner`` () =
    let file = path [ "repo"; "shared"; "Version.cs" ]

    Assert.Equal(None, ForeignFiles.owningProjectDirectory (projectsIn []) file)

[<Fact>]
let ``a file inside the closure's own project is not foreign`` () =
    let app = path [ "repo"; "App" ]
    let file = path [ "repo"; "App"; "Program.cs" ]

    Assert.Empty(ForeignFiles.detect (projectsIn [ app ]) [ app ] [ file ])

[<Fact>]
let ``a file nested under the closure's own project is not foreign`` () =
    let app = path [ "repo"; "App" ]
    let file = path [ "repo"; "App"; "Models"; "Order.cs" ]

    Assert.Empty(ForeignFiles.detect (projectsIn [ app ]) [ app ] [ file ])

[<Fact>]
let ``a file owned by a project outside the closure is foreign`` () =
    let app = path [ "repo"; "App" ]
    let other = path [ "repo"; "Other" ]
    let file = path [ "repo"; "Other"; "Swept.cs" ]

    let groups = ForeignFiles.detect (projectsIn [ app; other ]) [ app ] [ file ]

    Assert.Equal(1, groups.Length)
    Assert.Equal(other, groups.Head.Directory)
    Assert.Equal<string list>([ file ], groups.Head.Files)

[<Fact>]
let ``a file with no owning project is never foreign`` () =
    let app = path [ "repo"; "App" ]
    let file = path [ "repo"; "shared"; "Version.cs" ]

    Assert.Empty(ForeignFiles.detect (projectsIn [ app ]) [ app ] [ file ])

[<Fact>]
let ``files under one foreign project collapse into a single group`` () =
    let app = path [ "repo"; "App" ]
    let other = path [ "repo"; "Other" ]
    let first = path [ "repo"; "Other"; "A.cs" ]
    let second = path [ "repo"; "Other"; "Deep"; "B.cs" ]

    let groups = ForeignFiles.detect (projectsIn [ app; other ]) [ app ] [ first; second ]

    Assert.Equal(1, groups.Length)
    Assert.Equal<string list>([ first; second ], groups.Head.Files)

[<Fact>]
let ``two foreign projects yield one group each, in first-appearance order`` () =
    let app = path [ "repo"; "App" ]
    let bee = path [ "repo"; "Bee" ]
    let cee = path [ "repo"; "Cee" ]
    let fromBee = path [ "repo"; "Bee"; "B.cs" ]
    let fromCee = path [ "repo"; "Cee"; "C.cs" ]

    let groups =
        ForeignFiles.detect (projectsIn [ app; bee; cee ]) [ app ] [ fromCee; fromBee ]

    Assert.Equal<string list>([ cee; bee ], groups |> List.map (fun g -> g.Directory))

// Windows and macOS compare paths case-insensitively; a closure directory spelled differently
// from the resolved path must not turn its own files foreign.
[<Fact>]
let ``closure membership ignores case`` () =
    let app = path [ "repo"; "App" ]
    let shouty = path [ "repo"; "APP" ]
    let file = path [ "repo"; "App"; "Program.cs" ]

    Assert.Empty(ForeignFiles.detect (projectsIn [ app ]) [ shouty ] [ file ])

// A trailing separator is insignificant to the filesystem and must be insignificant here.
[<Fact>]
let ``closure membership ignores a trailing separator`` () =
    let app = path [ "repo"; "App" ]
    let file = path [ "repo"; "App"; "Program.cs" ]

    Assert.Empty(
        ForeignFiles.detect (projectsIn [ app ]) [ app + string System.IO.Path.DirectorySeparatorChar ] [ file ]
    )

// Segment-wise, not string-prefix: "AppTests" is not the "App" directory.
[<Fact>]
let ``a prefix-sharing sibling project is still foreign`` () =
    let app = path [ "repo"; "App" ]
    let appTests = path [ "repo"; "AppTests" ]
    let file = path [ "repo"; "AppTests"; "Tests.cs" ]

    let groups = ForeignFiles.detect (projectsIn [ app; appTests ]) [ app ] [ file ]

    Assert.Equal(1, groups.Length)
    Assert.Equal(appTests, groups.Head.Directory)
