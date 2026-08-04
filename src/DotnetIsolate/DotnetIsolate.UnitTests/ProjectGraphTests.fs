module DotnetIsolate.UnitTests.ProjectGraphTests

open Xunit
open DotnetIsolate.Core.ProjectGraph

let private resolverFrom (edges: Map<string, string list>) : ProjectReferenceResolver =
    fun project -> edges |> Map.tryFind project |> Option.defaultValue []

[<Fact>]
let ``a project with no references resolves to just itself`` () =
    let resolver = resolverFrom Map.empty

    let result = resolve resolver "A"

    Assert.Equal<string list>([ "A" ], result)

[<Fact>]
let ``a chain of references resolves every project in the chain`` () =
    let resolver = resolverFrom (Map [ "A", [ "B" ]; "B", [ "C" ] ])

    let result = resolve resolver "A"

    Assert.Equal<Set<string>>(Set [ "A"; "B"; "C" ], Set result)

[<Fact>]
let ``a diamond dependency is only included once`` () =
    // A -> B, A -> C, B -> D, C -> D
    let resolver =
        resolverFrom (Map [ "A", [ "B"; "C" ]; "B", [ "D" ]; "C", [ "D" ] ])

    let result = resolve resolver "A"

    Assert.Equal<Set<string>>(Set [ "A"; "B"; "C"; "D" ], Set result)
    Assert.Equal(4, result.Length)
    Assert.Equal(1, result |> List.filter ((=) "D") |> List.length)

[<Fact>]
let ``the entry project is always included even if unreachable from itself`` () =
    let resolver = resolverFrom Map.empty

    let result = resolve resolver "OnlyProject"

    Assert.Contains("OnlyProject", result)
