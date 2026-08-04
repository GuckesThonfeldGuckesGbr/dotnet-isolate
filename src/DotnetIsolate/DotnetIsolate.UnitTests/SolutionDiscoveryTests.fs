module DotnetIsolate.UnitTests.SolutionDiscoveryTests

open Xunit
open DotnetIsolate.Core.SolutionDiscovery

let private filesInFrom (contents: Map<string, string list>) : SolutionFilesInDirectory =
    fun dir -> contents |> Map.tryFind dir |> Option.defaultValue []

[<Fact>]
let ``finds a solution file in the starting directory itself`` () =
    let filesIn = filesInFrom (Map [ "/repo/src/Project", [ "/repo/src/Project/Project.sln" ] ])

    let result = resolveSolutionRoot filesIn None "/repo/src/Project"

    Assert.Equal(Some "/repo/src/Project/Project.sln", result |> Option.map (fun r -> r.SolutionFile))
    Assert.Equal(Some AutoDiscovered, result |> Option.map (fun r -> r.Source))

[<Fact>]
let ``walks up past directories with no solution file to find the nearest one`` () =
    let filesIn = filesInFrom (Map [ "/repo", [ "/repo/Repo.sln" ] ])

    let result = resolveSolutionRoot filesIn None "/repo/src/Project"

    Assert.Equal(Some "/repo/Repo.sln", result |> Option.map (fun r -> r.SolutionFile))
    Assert.Equal(Some "/repo", result |> Option.map (fun r -> r.Directory))

[<Fact>]
let ``returns none when no solution file is found before the filesystem root`` () =
    let filesIn = filesInFrom Map.empty

    let result = resolveSolutionRoot filesIn None "/repo/src/Project"

    Assert.Equal(None, result)

[<Fact>]
let ``an explicit solution path always wins over auto-discovery`` () =
    let filesIn = filesInFrom (Map [ "/repo", [ "/repo/Repo.sln" ] ])

    let result =
        resolveSolutionRoot filesIn (Some "/elsewhere/Other.sln") "/repo/src/Project"

    Assert.Equal(Some "/elsewhere/Other.sln", result |> Option.map (fun r -> r.SolutionFile))
    Assert.Equal(Some ExplicitlyProvided, result |> Option.map (fun r -> r.Source))

[<Fact>]
let ``picks the alphabetically-first solution file when a directory has more than one`` () =
    let filesIn =
        filesInFrom (
            Map [ "/repo", [ "/repo/Zed.sln"; "/repo/Alpha.sln" ] ]
        )

    let result = resolveSolutionRoot filesIn None "/repo"

    Assert.Equal(Some "/repo/Alpha.sln", result |> Option.map (fun r -> r.SolutionFile))
