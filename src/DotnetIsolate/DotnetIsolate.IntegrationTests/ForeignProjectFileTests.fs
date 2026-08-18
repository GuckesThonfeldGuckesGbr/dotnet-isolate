module DotnetIsolate.IntegrationTests.ForeignProjectFileTests

open System.IO
open Xunit
open DotnetIsolate.Core
open DotnetIsolate.IntegrationTests.TestFixtures

/// A glob reaching up out of its own directory sweeps in a file another project owns - FR-1's
/// "nothing more" violated by a real, legal MSBuild construct. FR-16 reports it and, deliberately,
/// changes nothing about what gets materialized.
[<Fact>]
let ``isolate reports a file owned by a project outside the closure without dropping it`` () =
    withTempDir (fun root ->
        let projectDir = Path.Combine(root, "A")
        Directory.CreateDirectory(projectDir) |> ignore

        File.WriteAllText(
            Path.Combine(projectDir, "A.fsproj"),
            """<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net8.0</TargetFramework>
  </PropertyGroup>
  <ItemGroup>
    <Compile Include="Program.fs"/>
    <None Include="../Foreign/**/*.json"/>
  </ItemGroup>
</Project>
"""
        )

        File.WriteAllText(Path.Combine(projectDir, "Program.fs"), "module A.Program\n")

        // A project outside A's closure - nothing references it - that owns the swept file.
        writeProject (Path.Combine(root, "Foreign")) "Foreign" [] []
        File.WriteAllText(Path.Combine(root, "Foreign", "swept.json"), "{}")

        let outputDir = Path.Combine(root, "output")

        let result =
            Pipeline.isolate
                { ProjectPaths = [ Path.Combine(projectDir, "A.fsproj") ]
                  OutputDir = Some outputDir
                  SolutionPath = None
                  RestoreOnly = false
                  Clean = false }

        // Reported, grouped under the owning project's directory. Compared with sameDirectory
        // rather than string equality: this path came back through MSBuild, and asserting on its
        // spelling would make the test hostage to separators and casing (see AGENTS.md).
        let group = result.ForeignProjectFiles |> List.exactlyOne
        Assert.True(MirrorRoot.sameDirectory (Path.Combine(root, "Foreign")) group.Directory)
        Assert.Contains(group.Files, fun (f: string) -> Path.GetFileName(f) = "swept.json")

        // Not dropped. This is the half that pins detection over exclusion: the rejected design
        // would leave the file out and still satisfy the assertion above.
        Assert.True(File.Exists(Path.Combine(outputDir, "Foreign", "swept.json")))
        Assert.DoesNotContain(result.ExcludedArtifacts, fun (f: string) -> Path.GetFileName(f) = "swept.json"))

/// The deliberate cross-project link FR-16 must stay silent about: a shared file in a directory no
/// project owns. Reporting it would fire on repos doing nothing wrong.
[<Fact>]
let ``isolate stays silent about a linked file no project directory owns`` () =
    withTempDir (fun root ->
        let projectDir = Path.Combine(root, "A")
        Directory.CreateDirectory(projectDir) |> ignore

        File.WriteAllText(
            Path.Combine(projectDir, "A.fsproj"),
            """<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net8.0</TargetFramework>
  </PropertyGroup>
  <ItemGroup>
    <Compile Include="../shared/Version.fs"/>
    <Compile Include="Program.fs"/>
  </ItemGroup>
</Project>
"""
        )

        File.WriteAllText(Path.Combine(projectDir, "Program.fs"), "module A.Program\n")
        Directory.CreateDirectory(Path.Combine(root, "shared")) |> ignore
        File.WriteAllText(Path.Combine(root, "shared", "Version.fs"), "module Shared.Version\n")

        let outputDir = Path.Combine(root, "output")

        let result =
            Pipeline.isolate
                { ProjectPaths = [ Path.Combine(projectDir, "A.fsproj") ]
                  OutputDir = Some outputDir
                  SolutionPath = None
                  RestoreOnly = false
                  Clean = false }

        Assert.Empty(result.ForeignProjectFiles)
        Assert.True(File.Exists(Path.Combine(outputDir, "shared", "Version.fs"))))

/// A project's own nested sources, and those of projects genuinely in the closure, are never
/// foreign - the rule must not fire on the ordinary case.
[<Fact>]
let ``isolate reports nothing when every file belongs to the closure`` () =
    withTempDir (fun root ->
        writeProject (Path.Combine(root, "A")) "A" [ "B" ] []
        writeProject (Path.Combine(root, "B")) "B" [] []

        let outputDir = Path.Combine(root, "output")

        let result =
            Pipeline.isolate
                { ProjectPaths = [ Path.Combine(root, "A", "A.fsproj") ]
                  OutputDir = Some outputDir
                  SolutionPath = None
                  RestoreOnly = false
                  Clean = false }

        Assert.Empty(result.ForeignProjectFiles))
