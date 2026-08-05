module DotnetIsolate.UnitTests.SolutionDiscoveryTests

open System.IO
open Xunit
open DotnetIsolate.Core.SolutionDiscovery
open DotnetIsolate.UnitTests.PathHelpers

let private filesInFrom (contents: Map<string, string list>) : SolutionFilesInDirectory =
    fun dir -> contents |> Map.tryFind dir |> Option.defaultValue []

[<Fact>]
let ``finds a solution file in the starting directory itself`` () =
    let projectDir = path [ "repo"; "src"; "Project" ]
    let slnFile = path [ "repo"; "src"; "Project"; "Project.sln" ]
    let filesIn = filesInFrom (Map [ projectDir, [ slnFile ] ])

    let result = resolveSolutionRoot filesIn None projectDir

    Assert.Equal(Some slnFile, result |> Option.map (fun r -> r.SolutionFile))
    Assert.Equal(Some AutoDiscovered, result |> Option.map (fun r -> r.Source))

[<Fact>]
let ``walks up past directories with no solution file to find the nearest one`` () =
    let repoDir = path [ "repo" ]
    let slnFile = path [ "repo"; "Repo.sln" ]
    let filesIn = filesInFrom (Map [ repoDir, [ slnFile ] ])

    let result = resolveSolutionRoot filesIn None (path [ "repo"; "src"; "Project" ])

    Assert.Equal(Some slnFile, result |> Option.map (fun r -> r.SolutionFile))
    Assert.Equal(Some repoDir, result |> Option.map (fun r -> r.Directory))

[<Fact>]
let ``returns none when no solution file is found before the filesystem root`` () =
    let filesIn = filesInFrom Map.empty

    let result = resolveSolutionRoot filesIn None (path [ "repo"; "src"; "Project" ])

    Assert.Equal(None, result)

[<Fact>]
let ``an explicit solution path always wins over auto-discovery`` () =
    let filesIn = filesInFrom (Map [ path [ "repo" ], [ path [ "repo"; "Repo.sln" ] ] ])
    let explicitSln = path [ "elsewhere"; "Other.sln" ]

    let result =
        resolveSolutionRoot filesIn (Some explicitSln) (path [ "repo"; "src"; "Project" ])

    // resolveSolutionRoot runs the explicit path through Path.GetFullPath, which on Windows
    // resolves a drive-relative literal like "\elsewhere\Other.sln" onto the current drive (e.g.
    // "D:\elsewhere\Other.sln") - so the expected value must be resolved the same way rather than
    // compared against the bare literal.
    Assert.Equal(Some(Path.GetFullPath(explicitSln)), result |> Option.map (fun r -> r.SolutionFile))
    Assert.Equal(Some ExplicitlyProvided, result |> Option.map (fun r -> r.Source))

[<Fact>]
let ``an explicit relative solution path is resolved to an absolute directory`` () =
    let filesIn = filesInFrom Map.empty
    let relativeSln = Path.Combine("sub", "MySolution.sln")

    let result = resolveSolutionRoot filesIn (Some relativeSln) (path [ "repo"; "src"; "Project" ])

    let directory = result |> Option.map (fun r -> r.Directory) |> Option.defaultValue ""
    Assert.True(Path.IsPathRooted(directory), $"expected an absolute directory, got '{directory}'")

[<Fact>]
let ``picks the alphabetically-first solution file when a directory has more than one`` () =
    let repoDir = path [ "repo" ]
    let alphaSln = path [ "repo"; "Alpha.sln" ]
    let zedSln = path [ "repo"; "Zed.sln" ]
    let filesIn = filesInFrom (Map [ repoDir, [ zedSln; alphaSln ] ])

    let result = resolveSolutionRoot filesIn None repoDir

    Assert.Equal(Some alphaSln, result |> Option.map (fun r -> r.SolutionFile))
