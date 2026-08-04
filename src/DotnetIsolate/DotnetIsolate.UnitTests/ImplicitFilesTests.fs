module DotnetIsolate.UnitTests.ImplicitFilesTests

open Xunit
open DotnetIsolate.Core.ImplicitFiles

let private filesInFrom (contents: Map<string, string list>) : FilesInDirectory =
    fun dir -> contents |> Map.tryFind dir |> Option.defaultValue []

[<Fact>]
let ``collects a well-known file found in the starting directory`` () =
    let filesIn = filesInFrom (Map [ "/repo/src/A", [ "/repo/src/A/Directory.Build.props" ] ])

    let result = resolve filesIn (Some "/repo/src/A") "/repo/src/A"

    Assert.Equal<string list>([ "/repo/src/A/Directory.Build.props" ], result)

[<Fact>]
let ``collects every occurrence while walking up to the ceiling, not just the nearest`` () =
    let filesIn =
        filesInFrom (
            Map
                [ "/repo/src/A", [ "/repo/src/A/Directory.Build.props" ]
                  "/repo/src", [ "/repo/src/Directory.Build.props" ]
                  "/repo", [ "/repo/Directory.Build.props"; "/repo/NuGet.config" ] ]
        )

    let result = resolve filesIn (Some "/repo") "/repo/src/A"

    Assert.Equal<Set<string>>(
        Set
            [ "/repo/src/A/Directory.Build.props"
              "/repo/src/Directory.Build.props"
              "/repo/Directory.Build.props"
              "/repo/NuGet.config" ],
        Set result
    )

[<Fact>]
let ``does not collect files above the ceiling`` () =
    let filesIn =
        filesInFrom (
            Map
                [ "/repo/src/A", []
                  "/repo", [ "/repo/NuGet.config" ]
                  "/", [ "/NuGet.config" ] ]
        )

    let result = resolve filesIn (Some "/repo") "/repo/src/A"

    Assert.DoesNotContain("/NuGet.config", result)
    Assert.Contains("/repo/NuGet.config", result)

[<Fact>]
let ``walks to the filesystem root when there is no ceiling`` () =
    let filesIn = filesInFrom (Map [ "/", [ "/NuGet.config" ] ])

    let result = resolve filesIn None "/repo/src/A"

    Assert.Contains("/NuGet.config", result)

[<Fact>]
let ``resolveForProjects dedups a shared file found via more than one project`` () =
    let filesIn = filesInFrom (Map [ "/repo", [ "/repo/Directory.Build.props" ] ])

    let result = resolveForProjects filesIn (Some "/repo") [ "/repo/src/A"; "/repo/src/B" ]

    Assert.Equal<string list>([ "/repo/Directory.Build.props" ], result)
