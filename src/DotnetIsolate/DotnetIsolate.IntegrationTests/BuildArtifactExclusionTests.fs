module DotnetIsolate.IntegrationTests.BuildArtifactExclusionTests

open System.IO
open Xunit
open DotnetIsolate.Core
open DotnetIsolate.IntegrationTests.TestFixtures

/// A project whose directory contains other projects globs their bin/obj in through the SDK's
/// default `**/*` None glob - DefaultItemExcludes only ever excludes a project's *own* output.
/// This is the layout that produced the original report.
[<Fact>]
let ``isolate excludes bin and obj artifacts of nested projects`` () =
    withTempDir (fun root ->
        writeProject (Path.Combine(root, "A")) "A" [ "B" ] []
        writeProject (Path.Combine(root, "B")) "B" [] []

        // Real artifacts, as a prior host-side build would leave them.
        let objDir = Path.Combine(root, "B", "obj")
        Directory.CreateDirectory(objDir) |> ignore
        File.WriteAllText(Path.Combine(objDir, "project.assets.json"), "{}")
        let binDir = Path.Combine(root, "B", "bin", "Debug", "net8.0")
        Directory.CreateDirectory(binDir) |> ignore
        File.WriteAllText(Path.Combine(binDir, "B.dll"), "MZ")

        let outputDir = Path.Combine(root, "output")

        let result =
            Pipeline.isolate
                { ProjectPaths = [ Path.Combine(root, "A", "A.fsproj") ]
                  OutputDir = Some outputDir
                  SolutionPath = None
                  RestoreOnly = false
                  Clean = false }

        Assert.True(File.Exists(Path.Combine(outputDir, "B", "B.fsproj")))
        Assert.False(Directory.Exists(Path.Combine(outputDir, "B", "obj")))
        Assert.False(Directory.Exists(Path.Combine(outputDir, "B", "bin")))

        // The project's own sources are never mistaken for artifacts.
        Assert.DoesNotContain(result.ExcludedArtifacts, fun (f: string) -> f.EndsWith("B.fsproj")))

/// A hand-written glob bypasses DefaultItemExcludes entirely, so even a leaf project can pull its
/// own obj/ in. The filter works on resolved paths, so it covers this mechanism identically.
[<Fact>]
let ``isolate excludes obj content pulled in by a hand-written glob`` () =
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
    <None Include="**/*.json"/>
  </ItemGroup>
</Project>
"""
        )

        File.WriteAllText(Path.Combine(projectDir, "Program.fs"), "module A.Program\n")
        File.WriteAllText(Path.Combine(projectDir, "appsettings.json"), "{}")
        Directory.CreateDirectory(Path.Combine(projectDir, "obj")) |> ignore
        File.WriteAllText(Path.Combine(projectDir, "obj", "project.assets.json"), "{}")

        let outputDir = Path.Combine(root, "output")

        let result =
            Pipeline.isolate
                { ProjectPaths = [ Path.Combine(projectDir, "A.fsproj") ]
                  OutputDir = Some outputDir
                  SolutionPath = None
                  RestoreOnly = false
                  Clean = false }

        // One project and no solution, so the mirror root is the project directory itself and its
        // files land directly at the output root.
        Assert.True(File.Exists(Path.Combine(outputDir, "appsettings.json")))
        Assert.False(Directory.Exists(Path.Combine(outputDir, "obj")))
        Assert.NotEmpty(result.ExcludedArtifacts))

/// `UseArtifactsOutput` relocates all output to a repo-root `artifacts/` tree whose `bin`/`obj`
/// directories have no project file beside them, so rule A cannot see them - only rule D's
/// ArtifactsPath can.
///
/// The fixture uses a hand-written glob deliberately, because that is the only way the artifacts
/// tree reaches the resolved set at all: with `UseArtifactsOutput` on, the SDK puts the *entire*
/// `$(ArtifactsPath)/**` into DefaultItemExcludes - not merely the evaluating project's own
/// subdirectory - so default globs never leak it (verified directly). A `<None Include="**/*"/>`
/// carries no such exclusion.
[<Fact>]
let ``isolate excludes the artifacts layout, which only rule D can see`` () =
    withTempDir (fun root ->
        File.WriteAllText(
            Path.Combine(root, "Directory.Build.props"),
            "<Project><PropertyGroup><UseArtifactsOutput>true</UseArtifactsOutput></PropertyGroup></Project>"
        )

        // A root-level project with a hand-written glob: the layout that actually leaks.
        File.WriteAllText(
            Path.Combine(root, "Root.csproj"),
            """<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net8.0</TargetFramework>
    <EnableDefaultCompileItems>false</EnableDefaultCompileItems>
  </PropertyGroup>
  <ItemGroup>
    <None Include="**/*.json"/>
  </ItemGroup>
</Project>
"""
        )

        File.WriteAllText(Path.Combine(root, "appsettings.json"), "{}")

        let artifactsObj = Path.Combine(root, "artifacts", "obj", "Root")
        Directory.CreateDirectory(artifactsObj) |> ignore
        File.WriteAllText(Path.Combine(artifactsObj, "project.assets.json"), "{}")

        let outputDir = Path.Combine(root, "output")

        let result =
            Pipeline.isolate
                { ProjectPaths = [ Path.Combine(root, "Root.csproj") ]
                  OutputDir = Some outputDir
                  SolutionPath = None
                  RestoreOnly = false
                  Clean = false }

        Assert.False(Directory.Exists(Path.Combine(outputDir, "artifacts")))

        // The genuine inputs are untouched.
        Assert.True(File.Exists(Path.Combine(outputDir, "Root.csproj")))
        Assert.True(File.Exists(Path.Combine(outputDir, "appsettings.json")))
        Assert.True(File.Exists(Path.Combine(outputDir, "Directory.Build.props")))

        Assert.Contains(result.ExcludedArtifacts, fun (f: string) -> f.EndsWith("project.assets.json")))
