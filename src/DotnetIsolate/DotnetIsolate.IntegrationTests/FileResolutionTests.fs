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
        let result =
            FileResolution.resolveFiles (FileResolutionIo.resolvers None) projectPath

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
            FileResolution.resolveAllFiles (FileResolutionIo.resolvers None) projects

        let fileNames = result |> List.map Path.GetFileName |> Set.ofList

        Assert.Equal<Set<string>>(
            Set [ "A.fsproj"; "B.fsproj"; "Program.fs"; "appsettings.json" ],
            fileNames
        )
        // Program.fs exists in both A and B - confirm it wasn't silently collapsed to one entry.
        Assert.Equal(5, result.Length))

[<Fact>]
let ``resolveFiles picks up analyzer inputs: AdditionalFiles items and the CodeAnalysisRuleSet property`` () =
    withTempDir (fun root ->
        // A StyleCop-shaped layout: the ruleset lives at the repo root, shared by every project,
        // and stylecop.json sits next to the project and is fed to analyzers via AdditionalFiles.
        File.WriteAllText(Path.Combine(root, "analysis.ruleset"), "<RuleSet Name=\"R\" ToolsVersion=\"16.0\" />")

        let projectDir = Path.Combine(root, "A")
        Directory.CreateDirectory(projectDir) |> ignore
        File.WriteAllText(Path.Combine(projectDir, "stylecop.json"), "{}")
        File.WriteAllText(Path.Combine(projectDir, "Program.fs"), "module A.Program")

        File.WriteAllText(
            Path.Combine(projectDir, "A.fsproj"),
            """<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net8.0</TargetFramework>
    <CodeAnalysisRuleSet>../analysis.ruleset</CodeAnalysisRuleSet>
  </PropertyGroup>
  <ItemGroup>
    <Compile Include="Program.fs"/>
    <AdditionalFiles Include="stylecop.json"/>
  </ItemGroup>
</Project>
"""
        )

        let result =
            FileResolution.resolveFiles
                (FileResolutionIo.resolvers None)
                (Path.Combine(projectDir, "A.fsproj"))

        let fileNames = result |> List.map Path.GetFileName |> Set.ofList

        Assert.Equal<Set<string>>(
            Set [ "A.fsproj"; "Program.fs"; "stylecop.json"; "analysis.ruleset" ],
            fileNames
        )

        // The ruleset path is relative to the project, so it must come back resolved against the
        // project directory - not the process's working directory.
        Assert.Contains(Path.Combine(root, "analysis.ruleset"), result))

/// Real-MSBuild proof of why `ancestorGlobbedItemTypes` needs a ceiling: the SDK's
/// `EditorConfigFiles` walk climbs past the solution root, so it resolves a `.editorconfig` that
/// lives *above* the repo as readily as the repo's own - and nested ones inside the project too,
/// which is exactly what FR-5's walk-up from the project directory cannot see.
///
/// Uses a csproj deliberately: verified directly that the F# SDK leaves `EditorConfigFiles` empty
/// at evaluation time while the C# SDK populates it, which is why FR-5 also carries
/// `.editorconfig` by name - that walk-up is language-agnostic and covers F# projects too.
[<Fact>]
let ``resolveFiles bounds EditorConfigFiles by the ceiling while keeping nested ones`` () =
    withTempDir (fun outer ->
        // "outer" stands in for a home directory: a .editorconfig here has no `root = true`, so
        // MSBuild's walk keeps going up through it.
        File.WriteAllText(Path.Combine(outer, ".editorconfig"), "# stray\n")

        let repo = Path.Combine(outer, "repo")
        let projectDir = Path.Combine(repo, "A")
        let nestedDir = Path.Combine(projectDir, "Generated")
        Directory.CreateDirectory(nestedDir) |> ignore

        File.WriteAllText(Path.Combine(repo, ".editorconfig"), "# repo\n")
        File.WriteAllText(Path.Combine(nestedDir, ".editorconfig"), "# nested\n")
        File.WriteAllText(Path.Combine(projectDir, "Program.cs"), "class Program {}")

        File.WriteAllText(
            Path.Combine(projectDir, "A.csproj"),
            """<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net8.0</TargetFramework>
  </PropertyGroup>
</Project>
"""
        )

        let projectPath = Path.Combine(projectDir, "A.csproj")

        // Unbounded, MSBuild really does hand back the stray one from above the repo.
        let unbounded = FileResolution.resolveFiles (FileResolutionIo.resolvers None) projectPath
        Assert.Contains(Path.Combine(outer, ".editorconfig"), unbounded)

        // Bounded by the solution root, only the in-repo ones survive - including the nested one.
        let bounded = FileResolution.resolveFiles (FileResolutionIo.resolvers (Some repo)) projectPath

        Assert.Contains(Path.Combine(repo, ".editorconfig"), bounded)
        Assert.Contains(Path.Combine(nestedDir, ".editorconfig"), bounded)
        Assert.DoesNotContain(Path.Combine(outer, ".editorconfig"), bounded))

/// FR-9 across the whole property list, via real MSBuild evaluation rather than a stub map.
[<Fact>]
let ``resolveFiles picks up every file-path property, and skips ones pointing at nothing`` () =
    withTempDir (fun root ->
        let projectDir = Path.Combine(root, "A")
        Directory.CreateDirectory(Path.Combine(projectDir, "assets")) |> ignore
        File.WriteAllText(Path.Combine(projectDir, "Program.fs"), "module A.Program")
        File.WriteAllText(Path.Combine(root, "sign.snk"), "")
        File.WriteAllText(Path.Combine(projectDir, "assets", "app.ico"), "")
        File.WriteAllText(Path.Combine(projectDir, "app.manifest"), "<assembly/>")

        File.WriteAllText(
            Path.Combine(projectDir, "A.fsproj"),
            """<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net8.0</TargetFramework>
    <AssemblyOriginatorKeyFile>../sign.snk</AssemblyOriginatorKeyFile>
    <ApplicationIcon>assets/app.ico</ApplicationIcon>
    <ApplicationManifest>app.manifest</ApplicationManifest>
    <Win32Resource>never-written.res</Win32Resource>
  </PropertyGroup>
  <ItemGroup>
    <Compile Include="Program.fs"/>
  </ItemGroup>
</Project>
"""
        )

        let result =
            FileResolution.resolveFiles (FileResolutionIo.resolvers None) (Path.Combine(projectDir, "A.fsproj"))

        Assert.Contains(Path.Combine(root, "sign.snk"), result)
        Assert.Contains(Path.Combine(projectDir, "assets", "app.ico"), result)
        Assert.Contains(Path.Combine(projectDir, "app.manifest"), result)
        // Set, but there is no such file - dropped rather than failing materialization later.
        Assert.DoesNotContain(Path.Combine(projectDir, "never-written.res"), result))
