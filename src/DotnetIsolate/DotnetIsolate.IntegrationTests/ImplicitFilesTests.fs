module DotnetIsolate.IntegrationTests.ImplicitFilesTests

open System.IO
open Xunit
open DotnetIsolate.Core
open DotnetIsolate.IntegrationTests.TestFixtures

[<Fact>]
let ``finds real Directory.Build.props and NuGet.config walking up to the solution root`` () =
    withTempDir (fun root ->
        let projectDir = Path.Combine(root, "src", "A")
        Directory.CreateDirectory(projectDir) |> ignore
        File.WriteAllText(Path.Combine(root, "NuGet.config"), "<configuration/>")
        File.WriteAllText(Path.Combine(root, "src", "Directory.Build.props"), "<Project/>")

        let result = ImplicitFiles.resolve ImplicitFilesIo.filesOnDisk (Some root) projectDir

        let fileNames = result |> List.map Path.GetFileName |> Set.ofList
        Assert.Equal<Set<string>>(Set [ "Directory.Build.props"; "NuGet.config" ], fileNames))
