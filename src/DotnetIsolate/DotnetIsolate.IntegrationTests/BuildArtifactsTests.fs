module DotnetIsolate.IntegrationTests.BuildArtifactsTests

open System.IO
open Xunit
open DotnetIsolate.Core
open DotnetIsolate.IntegrationTests.TestFixtures

[<Fact>]
let ``containsProjectFile sees a real project file and nothing else`` () =
    withTempDir (fun root ->
        let projectDir = Path.Combine(root, "App")
        let plainDir = Path.Combine(root, "tools")
        Directory.CreateDirectory(projectDir) |> ignore
        Directory.CreateDirectory(plainDir) |> ignore
        File.WriteAllText(Path.Combine(projectDir, "App.csproj"), "<Project/>")
        File.WriteAllText(Path.Combine(plainDir, "build.sh"), "echo hi")

        Assert.True(BuildArtifactsIo.containsProjectFile projectDir)
        Assert.False(BuildArtifactsIo.containsProjectFile plainDir)
        Assert.False(BuildArtifactsIo.containsProjectFile (Path.Combine(root, "does-not-exist"))))

[<Fact>]
let ``containsProjectFile recognises fsproj and vbproj too`` () =
    withTempDir (fun root ->
        let fsharpDir = Path.Combine(root, "F")
        let vbDir = Path.Combine(root, "V")
        Directory.CreateDirectory(fsharpDir) |> ignore
        Directory.CreateDirectory(vbDir) |> ignore
        File.WriteAllText(Path.Combine(fsharpDir, "F.fsproj"), "<Project/>")
        File.WriteAllText(Path.Combine(vbDir, "V.vbproj"), "<Project/>")

        Assert.True(BuildArtifactsIo.containsProjectFile fsharpDir)
        Assert.True(BuildArtifactsIo.containsProjectFile vbDir))
