module DotnetIsolate.IntegrationTests.PipelineTests

open System.IO
open Xunit
open DotnetIsolate.Core
open DotnetIsolate.IntegrationTests.TestFixtures

/// End-to-end: A -> B, C; B -> D; C -> D (the diamond fixture shape from REQUIREMENTS.md QP-3),
/// with a real solution file and a NuGet.config at the root to exercise implicit-file resolution
/// too. Isolates A, then proves the output is a genuinely valid, buildable solution - not just a
/// plausible-looking file tree.
[<Fact>]
let ``isolate produces a mirrored, buildable output for a diamond dependency graph`` () =
    withTempDir (fun root ->
        writeProject (Path.Combine(root, "A")) "A" [ "B"; "C" ] []
        writeProject (Path.Combine(root, "B")) "B" [ "D" ] []
        writeProject (Path.Combine(root, "C")) "C" [ "D" ] []
        writeProject (Path.Combine(root, "D")) "D" [] [ "appsettings.json" ]

        File.WriteAllText(Path.Combine(root, "NuGet.config"), "<configuration/>")

        writeSolution
            (Path.Combine(root, "Fixture.sln"))
            [ "A", "A/A.fsproj"; "B", "B/B.fsproj"; "C", "C/C.fsproj"; "D", "D/D.fsproj" ]

        let outputDir = Path.Combine(root, "output")

        let result =
            Pipeline.isolate
                { ProjectPaths = [ Path.Combine(root, "A", "A.fsproj") ]
                  OutputDir = Some outputDir
                  SolutionPath = None
                  RestoreOnly = false
                  Clean = false }

        // Every project's file made it into the mirrored tree.
        Assert.True(File.Exists(Path.Combine(outputDir, "A", "A.fsproj")))
        Assert.True(File.Exists(Path.Combine(outputDir, "B", "B.fsproj")))
        Assert.True(File.Exists(Path.Combine(outputDir, "C", "C.fsproj")))
        Assert.True(File.Exists(Path.Combine(outputDir, "D", "D.fsproj")))
        Assert.True(File.Exists(Path.Combine(outputDir, "D", "appsettings.json")))

        // The implicit repo-level file (FR-5) made it in too.
        Assert.True(File.Exists(Path.Combine(outputDir, "NuGet.config")))

        // The solution was auto-discovered (FR-8) and a scoped copy generated (FR-3).
        Assert.True(result.SolutionRoot.IsSome)
        Assert.Equal(SolutionDiscovery.AutoDiscovered, result.SolutionRoot.Value.Source)
        let outputSln = Path.Combine(outputDir, "Fixture.sln")
        Assert.True(File.Exists(outputSln))

        // The generated solution is genuinely valid: restore and build it for real.
        let exitCode, stdout, stderr =
            runDotnet outputDir [ "build"; outputSln; "-nodeReuse:false" ]
        Assert.True((exitCode = 0), $"dotnet build failed (exit {exitCode}):\n{stdout}\n{stderr}"))

/// FR-8: an explicit -s/--solution path always wins over auto-discovery, even when a different
/// solution would otherwise be found by walking up from the project.
[<Fact>]
let ``isolate uses an explicitly-provided solution path instead of auto-discovering one`` () =
    withTempDir (fun root ->
        writeProject (Path.Combine(root, "A")) "A" [] []

        // The auto-discoverable solution - must NOT be the one that ends up used.
        writeSolution (Path.Combine(root, "Nearest.sln")) [ "A", "A/A.fsproj" ]

        // The explicitly-requested solution, elsewhere entirely.
        let explicitSlnDir = Path.Combine(root, "elsewhere")
        Directory.CreateDirectory(explicitSlnDir) |> ignore
        let explicitSln = Path.Combine(explicitSlnDir, "Explicit.sln")
        writeSolution explicitSln [ "A", Path.Combine(root, "A", "A.fsproj") ]

        let result =
            Pipeline.isolate
                { ProjectPaths = [ Path.Combine(root, "A", "A.fsproj") ]
                  OutputDir = Some(Path.Combine(root, "output"))
                  SolutionPath = Some explicitSln
                  RestoreOnly = false
                  Clean = false }

        Assert.True(result.SolutionRoot.IsSome)
        Assert.Equal(SolutionDiscovery.ExplicitlyProvided, result.SolutionRoot.Value.Source)
        Assert.Equal(explicitSln, result.SolutionRoot.Value.SolutionFile))

/// FR-6: with no explicit -o/--output-dir, the output lands at ./<ProjectName> relative to the
/// current directory.
[<Fact>]
let ``isolate defaults the output directory to ./<ProjectName> when none is given`` () =
    withTempDir (fun root ->
        // Nested under "src" so the default output dir ("./A" relative to `root`) can't collide
        // with the project's own directory - a collision would have Materialize's delete/recreate
        // (FR-7) wipe the source project before copying it.
        writeProject (Path.Combine(root, "src", "A")) "A" [] []

        let previousCwd = Directory.GetCurrentDirectory()
        Directory.SetCurrentDirectory(root)

        let result =
            try
                Pipeline.isolate
                    { ProjectPaths = [ Path.Combine(root, "src", "A", "A.fsproj") ]
                      OutputDir = None
                      SolutionPath = None
                      RestoreOnly = false
                      Clean = false }
            finally
                Directory.SetCurrentDirectory(previousCwd)

        Assert.Equal(Path.Combine(root, "A"), result.OutputDir)
        Assert.True(File.Exists(Path.Combine(root, "A", "A.fsproj"))))

/// FR-4 regression: an analyzer setup - a repo-root `.ruleset` referenced by the
/// `<CodeAnalysisRuleSet>` *property* (not an item, so `-getItem` alone never sees it) and a
/// `stylecop.json` carried by `<AdditionalFiles>` - must land in the isolated output. Without
/// both, the isolated build fails outright ("could not open rule set file") rather than silently
/// degrading, which is exactly why they can't be treated as optional extras.
[<Fact>]
let ``isolate copies analyzer inputs: the CodeAnalysisRuleSet file and AdditionalFiles`` () =
    withTempDir (fun root ->
        File.WriteAllText(
            Path.Combine(root, "analysis.ruleset"),
            """<?xml version="1.0" encoding="utf-8"?>
<RuleSet Name="Fixture" ToolsVersion="16.0" />
"""
        )

        // The ruleset is injected the way real repos do it - once, for every project - while each
        // project brings its own stylecop.json next to itself.
        File.WriteAllText(
            Path.Combine(root, "Directory.Build.props"),
            """<Project>
  <PropertyGroup>
    <CodeAnalysisRuleSet>$(MSBuildThisFileDirectory)analysis.ruleset</CodeAnalysisRuleSet>
  </PropertyGroup>
</Project>
"""
        )

        for name in [ "A"; "B" ] do
            let dir = Path.Combine(root, name)
            Directory.CreateDirectory(dir) |> ignore
            File.WriteAllText(Path.Combine(dir, "stylecop.json"), "{}")
            File.WriteAllText(Path.Combine(dir, "Program.fs"), $"module {name}.Program")

            let reference =
                if name = "A" then """<ProjectReference Include="../B/B.fsproj"/>""" else ""

            File.WriteAllText(
                Path.Combine(dir, $"{name}.fsproj"),
                $"""<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net8.0</TargetFramework>
  </PropertyGroup>
  <ItemGroup>
    <Compile Include="Program.fs"/>
    <AdditionalFiles Include="stylecop.json"/>
    {reference}
  </ItemGroup>
</Project>
"""
            )

        writeSolution (Path.Combine(root, "Fixture.sln")) [ "A", "A/A.fsproj"; "B", "B/B.fsproj" ]

        let outputDir = Path.Combine(root, "output")

        Pipeline.isolate
            { ProjectPaths = [ Path.Combine(root, "A", "A.fsproj") ]
              OutputDir = Some outputDir
              SolutionPath = None
              RestoreOnly = false
              Clean = false }
        |> ignore

        Assert.True(File.Exists(Path.Combine(outputDir, "analysis.ruleset")), "the CodeAnalysisRuleSet file")
        Assert.True(File.Exists(Path.Combine(outputDir, "A", "stylecop.json")), "A's AdditionalFiles entry")
        Assert.True(File.Exists(Path.Combine(outputDir, "B", "stylecop.json")), "B's AdditionalFiles entry")

        // And the isolated tree still builds - proving the ruleset resolves from its mirrored
        // location, not just that a file with the right name was copied somewhere.
        let outputSln = Path.Combine(outputDir, "Fixture.sln")

        let exitCode, stdout, stderr =
            runDotnet outputDir [ "build"; outputSln; "-nodeReuse:false" ]

        Assert.True((exitCode = 0), $"dotnet build failed (exit {exitCode}):\n{stdout}\n{stderr}"))

// The destructive reproduction: -o pointing at the solution root used to delete the whole source
// tree and only then fail. It must now fail before touching anything.
[<Fact>]
let ``isolate refuses an output directory that contains every input, leaving the source intact`` () =
    withTempDir (fun root ->
        writeProject (Path.Combine(root, "A")) "A" [] []
        writeSolution (Path.Combine(root, "Fixture.sln")) [ "A", "A/A.fsproj" ]

        let ex =
            Assert.ThrowsAny<exn>(fun () ->
                Pipeline.isolate
                    { ProjectPaths = [ Path.Combine(root, "A", "A.fsproj") ]
                      OutputDir = Some root
                      SolutionPath = None
                      RestoreOnly = false
                      Clean = false }
                |> ignore)

        Assert.Contains("output directory", ex.Message)
        // The source tree must still be there - this is the data-loss regression guard.
        Assert.True(File.Exists(Path.Combine(root, "A", "A.fsproj")))
        Assert.True(File.Exists(Path.Combine(root, "Fixture.sln"))))

// The re-ingestion reproduction: an output directory nested inside a project directory is swept
// up by the SDK's default globs on the second run.
//
// The F# SDK (unlike C#'s) sets EnableDefaultCompileItems/EnableDefaultNoneItems to false, so
// Compile/Content/None never auto-glob - verified directly against the pinned 8.0.x SDK. Its
// EmbeddedResource default glob (**/*.resx) is still on, though, so a .resx is what actually
// triggers the sweep-up here: materializing copies it into out/A/Resource.resx, and the second
// run's default glob over A's directory tree picks that copy back up as a second EmbeddedResource
// item, alongside the real one.
[<Fact>]
let ``isolate succeeds twice with an output directory nested inside a project directory`` () =
    withTempDir (fun root ->
        writeProject (Path.Combine(root, "A")) "A" [] []
        File.WriteAllText(Path.Combine(root, "A", "Resource.resx"), "<root></root>")
        writeSolution (Path.Combine(root, "Fixture.sln")) [ "A", "A/A.fsproj" ]

        let outputDir = Path.Combine(root, "A", "out")

        let options: Pipeline.IsolateOptions =
            { ProjectPaths = [ Path.Combine(root, "A", "A.fsproj") ]
              OutputDir = Some outputDir
              SolutionPath = None
              RestoreOnly = false
              Clean = false }

        Pipeline.isolate options |> ignore
        let second = Pipeline.isolate options

        Assert.True(File.Exists(Path.Combine(outputDir, "A", "A.fsproj")))
        // Nothing was placed inside itself a second level down.
        Assert.False(Directory.Exists(Path.Combine(outputDir, "A", "out")))
        Assert.NotEmpty(second.ExcludedUnderOutput))

[<Fact>]
let ``isolate reports pre-existing output entries as stale rather than deleting them`` () =
    withTempDir (fun root ->
        writeProject (Path.Combine(root, "A")) "A" [] []
        writeSolution (Path.Combine(root, "Fixture.sln")) [ "A", "A/A.fsproj" ]

        let outputDir = Path.Combine(root, "output")
        Directory.CreateDirectory(outputDir) |> ignore
        File.WriteAllText(Path.Combine(outputDir, "leftover.txt"), "previous run")

        let result =
            Pipeline.isolate
                { ProjectPaths = [ Path.Combine(root, "A", "A.fsproj") ]
                  OutputDir = Some outputDir
                  SolutionPath = None
                  RestoreOnly = false
                  Clean = false }

        Assert.True(File.Exists(Path.Combine(outputDir, "leftover.txt")))
        Assert.Contains(result.StaleEntries, fun e -> Path.GetFileName(e) = "leftover.txt"))

[<Fact>]
let ``isolate with Clean removes pre-existing output entries`` () =
    withTempDir (fun root ->
        writeProject (Path.Combine(root, "A")) "A" [] []
        writeSolution (Path.Combine(root, "Fixture.sln")) [ "A", "A/A.fsproj" ]

        let outputDir = Path.Combine(root, "output")
        Directory.CreateDirectory(outputDir) |> ignore
        File.WriteAllText(Path.Combine(outputDir, "leftover.txt"), "previous run")

        Pipeline.isolate
            { ProjectPaths = [ Path.Combine(root, "A", "A.fsproj") ]
              OutputDir = Some outputDir
              SolutionPath = None
              RestoreOnly = false
              Clean = true }
        |> ignore

        Assert.False(File.Exists(Path.Combine(outputDir, "leftover.txt"))))

[<Fact>]
let ``isolate warns about a referenced file that does not exist instead of failing`` () =
    withTempDir (fun root ->
        writeProject (Path.Combine(root, "A")) "A" [] []
        writeSolution (Path.Combine(root, "Fixture.sln")) [ "A", "A/A.fsproj" ]

        // Reference a file that is never created - the .dockerignore reproduction.
        let projectFile = Path.Combine(root, "A", "A.fsproj")
        let content = File.ReadAllText(projectFile)

        File.WriteAllText(
            projectFile,
            content.Replace("</Project>", "<ItemGroup><None Include=\"absent.txt\"/></ItemGroup></Project>")
        )

        let result =
            Pipeline.isolate
                { ProjectPaths = [ projectFile ]
                  OutputDir = Some(Path.Combine(root, "output"))
                  SolutionPath = None
                  RestoreOnly = false
                  Clean = false }

        Assert.Contains(result.MissingFiles, fun f -> Path.GetFileName(f) = "absent.txt"))
