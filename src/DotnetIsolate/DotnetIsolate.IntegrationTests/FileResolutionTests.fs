module DotnetIsolate.IntegrationTests.FileResolutionTests

open System.IO
open Xunit
open DotnetIsolate.Core
open DotnetIsolate.IntegrationTests.TestFixtures

[<Fact>]
let ``resolveFiles picks up Compile and Content items plus the project file via real MSBuild evaluation`` () =
    withTempDir (fun root ->
        writeProject (Path.Combine(root, "A")) "A" [] [ "appsettings.json" ]

        let projectPath = Path.Combine(root, "A", "A.fsproj")
        let result = FileResolution.resolveFiles FileResolutionIo.projectItemsResolver projectPath

        let fileNames = result |> List.map Path.GetFileName |> Set.ofList

        Assert.Equal<Set<string>>(Set [ "A.fsproj"; "Program.fs"; "appsettings.json" ], fileNames))

[<Fact>]
let ``resolveAllFiles aggregates real files, including project files, across a project graph`` () =
    withTempDir (fun root ->
        writeProject (Path.Combine(root, "A")) "A" [ "B" ] []
        writeProject (Path.Combine(root, "B")) "B" [] [ "appsettings.json" ]

        let entryProject = Path.Combine(root, "A", "A.fsproj")

        let projects =
            ProjectGraph.resolve MsBuild.projectReferenceResolver entryProject

        let result =
            FileResolution.resolveAllFiles FileResolutionIo.projectItemsResolver projects

        let fileNames = result |> List.map Path.GetFileName |> Set.ofList

        Assert.Equal<Set<string>>(
            Set [ "A.fsproj"; "B.fsproj"; "Program.fs"; "appsettings.json" ],
            fileNames
        )
        // Program.fs exists in both A and B - confirm it wasn't silently collapsed to one entry.
        Assert.Equal(5, result.Length))
