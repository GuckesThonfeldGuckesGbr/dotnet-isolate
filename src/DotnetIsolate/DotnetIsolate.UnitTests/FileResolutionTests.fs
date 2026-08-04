module DotnetIsolate.UnitTests.FileResolutionTests

open Xunit
open DotnetIsolate.Core.FileResolution

let private itemsFrom (projects: Map<string, Map<string, string list>>) : ProjectItemsResolver =
    fun project -> projects |> Map.tryFind project |> Option.defaultValue Map.empty

[<Fact>]
let ``resolveFiles includes the project file itself, since -getItem never returns it`` () =
    let getItems = itemsFrom Map.empty

    let result = resolveFiles getItems "A/A.fsproj"

    Assert.Equal<string list>([ "A/A.fsproj" ], result)

[<Fact>]
let ``resolveFiles flattens every item type plus the project file into one deduplicated list`` () =
    let getItems =
        itemsFrom (
            Map
                [ "A/A.fsproj",
                  Map [ "Compile", [ "A/Program.fs"; "A/Lib.fs" ]; "Content", [ "A/appsettings.json" ] ] ]
        )

    let result = resolveFiles getItems "A/A.fsproj"

    Assert.Equal<Set<string>>(
        Set [ "A/A.fsproj"; "A/Program.fs"; "A/Lib.fs"; "A/appsettings.json" ],
        Set result
    )

[<Fact>]
let ``resolveFiles dedups a file that appears under more than one item type`` () =
    let getItems =
        itemsFrom (Map [ "A/A.fsproj", Map [ "Compile", [ "A/Program.fs" ]; "None", [ "A/Program.fs" ] ] ])

    let result = resolveFiles getItems "A/A.fsproj"

    Assert.Equal<string list>([ "A/A.fsproj"; "A/Program.fs" ], result)

[<Fact>]
let ``resolveAllFiles aggregates and dedups files, including each project file, across multiple projects`` () =
    let getItems =
        itemsFrom (
            Map
                [ "A/A.fsproj", Map [ "Compile", [ "A/Program.fs" ] ]
                  "B/B.fsproj", Map [ "Compile", [ "B/Program.fs" ]; "Content", [ "Shared/settings.json" ] ]
                  "C/C.fsproj", Map [ "Content", [ "Shared/settings.json" ] ] ]
        )

    let result = resolveAllFiles getItems [ "A/A.fsproj"; "B/B.fsproj"; "C/C.fsproj" ]

    Assert.Equal<Set<string>>(
        Set
            [ "A/A.fsproj"
              "A/Program.fs"
              "B/B.fsproj"
              "B/Program.fs"
              "C/C.fsproj"
              "Shared/settings.json" ],
        Set result
    )
