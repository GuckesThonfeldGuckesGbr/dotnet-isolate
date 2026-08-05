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
                { ProjectPath = Path.Combine(root, "A", "A.fsproj")
                  OutputDir = Some outputDir
                  SolutionPath = None }

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
                { ProjectPath = Path.Combine(root, "A", "A.fsproj")
                  OutputDir = Some(Path.Combine(root, "output"))
                  SolutionPath = Some explicitSln }

        Assert.True(result.SolutionRoot.IsSome)
        Assert.Equal(SolutionDiscovery.ExplicitlyProvided, result.SolutionRoot.Value.Source)
        Assert.Equal(explicitSln, result.SolutionRoot.Value.SolutionFile))
