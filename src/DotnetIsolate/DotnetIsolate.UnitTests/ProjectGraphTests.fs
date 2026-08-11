module DotnetIsolate.UnitTests.ProjectGraphTests

open Xunit
open DotnetIsolate.Core.ProjectGraph
open DotnetIsolate.UnitTests.PathHelpers

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

// Two entry projects sharing a dependency: the union, with the shared project appearing once.
[<Fact>]
let ``resolveMany returns the deduplicated union of two overlapping closures`` () =
    let login = path [ "repo"; "Login"; "Login.fsproj" ]
    let loginQs = path [ "repo"; "LoginQs"; "LoginQs.fsproj" ]
    let shared = path [ "repo"; "Shared"; "Shared.fsproj" ]

    let references p =
        if p = login then [ shared ]
        elif p = loginQs then [ shared ]
        else []

    let result = resolveMany references [ login; loginQs ]

    Assert.Equal(3, result.Length)
    Assert.Contains(login, result)
    Assert.Contains(loginQs, result)
    Assert.Contains(shared, result)

// The exact anti-pattern resolveMany exists to avoid: resolving each entry separately and
// concatenating would pass the two tests above (they only check the returned set) while still
// re-spawning MSBuild once per entry for every shared dependency. This counts resolver
// invocations directly, the way the diamond-dependency counting test above does for `resolve`.
[<Fact>]
let ``resolveMany invokes the resolver exactly once for a dependency shared by two entries`` () =
    let login = path [ "repo"; "Login"; "Login.fsproj" ]
    let loginQs = path [ "repo"; "LoginQs"; "LoginQs.fsproj" ]
    let shared = path [ "repo"; "Shared"; "Shared.fsproj" ]

    let callCounts = System.Collections.Concurrent.ConcurrentDictionary<string, int>()
    let edges = Map [ login, [ shared ]; loginQs, [ shared ]; shared, [] ]

    let countingResolver: ProjectReferenceResolver =
        fun project ->
            callCounts.AddOrUpdate(project, 1, (fun _ n -> n + 1)) |> ignore
            edges |> Map.tryFind project |> Option.defaultValue []

    let result = resolveMany countingResolver [ login; loginQs ]

    Assert.Equal<Set<string>>(Set [ login; loginQs; shared ], Set result)

    for project in [ login; loginQs; shared ] do
        Assert.Equal(1, callCounts.[project])

[<Fact>]
let ``resolveMany with a single entry matches resolve`` () =
    let a = path [ "repo"; "A"; "A.fsproj" ]
    let b = path [ "repo"; "B"; "B.fsproj" ]
    let references p = if p = a then [ b ] else []

    Assert.Equal<string list>(resolve references a, resolveMany references [ a ])
