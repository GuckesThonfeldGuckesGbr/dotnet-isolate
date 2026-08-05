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

/// PR-1 (O(depth) rounds, not O(project count)) only holds if a shared dependency is evaluated
/// - i.e. would trigger a real MSBuild spawn in production - exactly once, not once per incoming
/// reference. Checking just the result set (as the older "only included once" test above does)
/// isn't enough to prove that on its own; this counts actual resolver invocations directly.
[<Fact>]
let ``the resolver is invoked exactly once per distinct project, even across a diamond dependency`` () =
    let callCounts = System.Collections.Concurrent.ConcurrentDictionary<string, int>()
    let edges = Map [ "A", [ "B"; "C" ]; "B", [ "D" ]; "C", [ "D" ]; "D", [] ]

    let countingResolver: ProjectReferenceResolver =
        fun project ->
            callCounts.AddOrUpdate(project, 1, (fun _ n -> n + 1)) |> ignore
            edges |> Map.tryFind project |> Option.defaultValue []

    let result = resolve countingResolver "A"

    Assert.Equal<Set<string>>(Set [ "A"; "B"; "C"; "D" ], Set result)

    for project in [ "A"; "B"; "C"; "D" ] do
        Assert.Equal(1, callCounts.[project])

/// Levels are evaluated concurrently (see ProjectGraph.fs), so this stresses that fan-out/fan-in
/// dedup under real thread-pool parallelism: 200 sibling projects at one level all reference the
/// same shared dependency, which must still show up - and be resolved - exactly once.
[<Fact>]
let ``a shared dependency discovered concurrently by many sibling projects at the same level is only included once``
    ()
    =
    let siblings = [ for i in 1..200 -> $"Sibling{i}" ]
    let callCounts = System.Collections.Concurrent.ConcurrentDictionary<string, int>()

    let edges =
        Map(("Root", siblings) :: [ for s in siblings -> s, [ "Shared" ] ])

    let countingResolver: ProjectReferenceResolver =
        fun project ->
            callCounts.AddOrUpdate(project, 1, (fun _ n -> n + 1)) |> ignore
            edges |> Map.tryFind project |> Option.defaultValue []

    let result = resolve countingResolver "Root"

    Assert.Equal<Set<string>>(Set("Root" :: "Shared" :: siblings), Set result)
    Assert.Equal(202, result.Length)
    Assert.Equal(202, callCounts.Count)

    // The whole point of level-parallel evaluation: every distinct project, including "Shared"
    // (concurrently discovered by all 200 siblings), is resolved exactly once - not 200 times.
    for project in "Root" :: "Shared" :: siblings do
        Assert.Equal(1, callCounts.[project])
