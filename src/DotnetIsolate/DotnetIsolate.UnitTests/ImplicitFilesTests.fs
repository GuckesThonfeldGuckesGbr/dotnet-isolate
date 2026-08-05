module DotnetIsolate.UnitTests.ImplicitFilesTests

open Xunit
open DotnetIsolate.Core.ImplicitFiles
open DotnetIsolate.UnitTests.PathHelpers

let private filesInFrom (contents: Map<string, string list>) : FilesInDirectory =
    fun dir -> contents |> Map.tryFind dir |> Option.defaultValue []

[<Fact>]
let ``collects a well-known file found in the starting directory`` () =
    let dirA = path [ "repo"; "src"; "A" ]
    let propsA = path [ "repo"; "src"; "A"; "Directory.Build.props" ]
    let filesIn = filesInFrom (Map [ dirA, [ propsA ] ])

    let result = resolve filesIn (Some dirA) dirA

    Assert.Equal<string list>([ propsA ], result)

[<Fact>]
let ``collects every occurrence while walking up to the ceiling, not just the nearest`` () =
    let dirA = path [ "repo"; "src"; "A" ]
    let dirSrc = path [ "repo"; "src" ]
    let dirRepo = path [ "repo" ]
    let propsA = path [ "repo"; "src"; "A"; "Directory.Build.props" ]
    let propsSrc = path [ "repo"; "src"; "Directory.Build.props" ]
    let propsRepo = path [ "repo"; "Directory.Build.props" ]
    let nugetRepo = path [ "repo"; "NuGet.config" ]

    let filesIn =
        filesInFrom (Map [ dirA, [ propsA ]; dirSrc, [ propsSrc ]; dirRepo, [ propsRepo; nugetRepo ] ])

    let result = resolve filesIn (Some dirRepo) dirA

    Assert.Equal<Set<string>>(Set [ propsA; propsSrc; propsRepo; nugetRepo ], Set result)

[<Fact>]
let ``does not collect files above the ceiling`` () =
    let dirA = path [ "repo"; "src"; "A" ]
    let dirRepo = path [ "repo" ]
    let root = path []
    let nugetRepo = path [ "repo"; "NuGet.config" ]
    let nugetRoot = path [ "NuGet.config" ]

    let filesIn = filesInFrom (Map [ dirA, []; dirRepo, [ nugetRepo ]; root, [ nugetRoot ] ])

    let result = resolve filesIn (Some dirRepo) dirA

    Assert.DoesNotContain(nugetRoot, result)
    Assert.Contains(nugetRepo, result)

[<Fact>]
let ``walks to the filesystem root when there is no ceiling`` () =
    let root = path []
    let nugetRoot = path [ "NuGet.config" ]
    let filesIn = filesInFrom (Map [ root, [ nugetRoot ] ])

    let result = resolve filesIn None (path [ "repo"; "src"; "A" ])

    Assert.Contains(nugetRoot, result)

[<Fact>]
let ``resolveForProjects dedups a shared file found via more than one project`` () =
    let dirRepo = path [ "repo" ]
    let propsRepo = path [ "repo"; "Directory.Build.props" ]
    let filesIn = filesInFrom (Map [ dirRepo, [ propsRepo ] ])

    let result =
        resolveForProjects filesIn (Some dirRepo) [ path [ "repo"; "src"; "A" ]; path [ "repo"; "src"; "B" ] ]

    Assert.Equal<string list>([ propsRepo ], result)
