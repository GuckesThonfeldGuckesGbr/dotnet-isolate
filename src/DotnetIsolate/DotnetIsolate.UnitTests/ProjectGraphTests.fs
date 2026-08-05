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

/// Levels are evaluated concurrently (see ProjectGraph.fs), so this stresses that fan-out/fan-in
/// dedup under real thread-pool parallelism: 200 sibling projects at one level all reference the
/// same shared dependency, which must still show up exactly once.
[<Fact>]
let ``a shared dependency discovered concurrently by many sibling projects at the same level is only included once``
    ()
    =
    let siblings = [ for i in 1..200 -> $"Sibling{i}" ]

    let resolver =
        resolverFrom (
            Map(("Root", siblings) :: [ for s in siblings -> s, [ "Shared" ] ])
        )

    let result = resolve resolver "Root"

    Assert.Equal<Set<string>>(Set("Root" :: "Shared" :: siblings), Set result)
    Assert.Equal(1, result |> List.filter ((=) "Shared") |> List.length)
    Assert.Equal(202, result.Length)
