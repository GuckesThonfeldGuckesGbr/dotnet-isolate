module DotnetIsolate.UnitTests.FileResolutionTests

open Xunit
open DotnetIsolate.Core.FileResolution

let private itemsFrom (projects: Map<string, Map<string, string list>>) : ProjectItemsResolver =
    fun project -> projects |> Map.tryFind project |> Option.defaultValue Map.empty

[<Fact>]
let ``resolveFiles flattens every item type into one deduplicated list`` () =
    let getItems =
        itemsFrom (
            Map
                [ "A",
                  Map [ "Compile", [ "A/Program.fs"; "A/Lib.fs" ]; "Content", [ "A/appsettings.json" ] ] ]
        )

    let result = resolveFiles getItems "A"

    Assert.Equal<Set<string>>(Set [ "A/Program.fs"; "A/Lib.fs"; "A/appsettings.json" ], Set result)

[<Fact>]
let ``resolveFiles dedups a file that appears under more than one item type`` () =
    let getItems =
        itemsFrom (Map [ "A", Map [ "Compile", [ "A/Program.fs" ]; "None", [ "A/Program.fs" ] ] ])

    let result = resolveFiles getItems "A"

    Assert.Equal<string list>([ "A/Program.fs" ], result)

[<Fact>]
let ``resolveFiles returns an empty list for a project with no build-relevant files`` () =
    let getItems = itemsFrom Map.empty

    let result = resolveFiles getItems "A"

    Assert.Empty(result)

[<Fact>]
let ``resolveAllFiles aggregates and dedups files across multiple projects`` () =
    let getItems =
        itemsFrom (
            Map
                [ "A", Map [ "Compile", [ "A/Program.fs" ] ]
                  "B", Map [ "Compile", [ "B/Program.fs" ]; "Content", [ "Shared/settings.json" ] ]
                  "C", Map [ "Content", [ "Shared/settings.json" ] ] ]
        )

    let result = resolveAllFiles getItems [ "A"; "B"; "C" ]

    Assert.Equal<Set<string>>(
        Set [ "A/Program.fs"; "B/Program.fs"; "Shared/settings.json" ],
        Set result
    )
