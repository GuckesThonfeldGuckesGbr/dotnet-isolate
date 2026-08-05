module DotnetIsolate.E2ETests.DockerCacheTests

open System
open System.IO
open Xunit
open DotnetIsolate.Core
open DotnetIsolate.E2ETests.DockerHarness

/// .../DotnetIsolate.E2ETests/bin/Release/net8.0 -> .../src
let private repoSrcDir =
    Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../"))

/// net8.0, so no SDK-version gymnastics are needed for the Docker images themselves.
let private fixtureSourceDir =
    Path.Combine(repoSrcDir, "TestSolutions", "DiamondWithIncludedFilesSln")

let private selfContainedDockerfile =
    Path.Combine(AppContext.BaseDirectory, "Fixtures", "SelfContained", "Dockerfile")

let private hostSideDockerfile =
    Path.Combine(AppContext.BaseDirectory, "Fixtures", "HostSide", "Dockerfile")

let private toolProjectPath =
    Path.Combine(repoSrcDir, "DotnetIsolate", "DotnetIsolate", "DotnetIsolate.fsproj")

let private freshTempDir () =
    Path.Combine(Path.GetTempPath(), "dotnet-isolate-e2e", Path.GetRandomFileName())

/// QP-12: builds twice back to back, with an unrelated file changed in between, and asserts the
/// expensive restore/build/publish layers cache-hit on the second build - the DI-1 mechanism
/// (COPY --from=<stage> is cached by checksumming copied bytes, not by chaining off the source
/// stage's own layer history) under both the classic builder and BuildKit.
[<Theory>]
[<InlineData(true)>]
[<InlineData(false)>]
let ``self-contained pattern is cache-hit on rebuild after an unrelated change`` (useBuildKit: bool) =
    Assert.True(File.Exists(Path.Combine(fixtureSourceDir, "DiamondWithIncludedFilesSln.sln")))

    let contextDir = copyFixtureToTempDir fixtureSourceDir
    let tag = $"dotnet-isolate-e2e-selfcontained-{Guid.NewGuid():N}"

    try
        packLocalTool toolProjectPath (Path.Combine(contextDir, "nupkg"))

        let exitCode1, output1 = runDockerBuild useBuildKit contextDir selfContainedDockerfile tag
        Assert.True((exitCode1 = 0), $"first build failed:\n{output1}")

        // Change a file outside ServiceA's dependency closure.
        touchUnrelatedFile (Path.Combine(contextDir, "ServiceB", "Program.cs"))

        let exitCode2, output2 = runDockerBuild useBuildKit contextDir selfContainedDockerfile tag
        Assert.True((exitCode2 = 0), $"second build failed:\n{output2}")

        // Sanity: the unrelated change really did invalidate the early, uncacheable step -
        // otherwise the cache-hit assertion below would be vacuous.
        Assert.True(
            stepNotCached useBuildKit output2 "dotnet isolate",
            $"expected the isolate step to rerun after an unrelated source change:\n{output2}"
        )

        // The actual DI-1 assertion: despite the isolate stage rerunning, the downstream
        // restore/build/publish layers still cache-hit.
        Assert.True(
            expensiveLayersCached useBuildKit output2,
            $"expected restore/build/publish layers to cache-hit:\n{output2}"
        )
    finally
        removeImage tag
        deleteIfExists contextDir

/// Same QP-12 assertion for the host-side pattern: the harness calls Pipeline.isolate in-process
/// (rather than shelling out to the CLI) before each build, into a brand-new output directory
/// each time - REL-1 determinism means its content is byte-identical despite living at an
/// entirely different path, so COPY . /src and every downstream layer still cache-hit.
[<Theory>]
[<InlineData(true)>]
[<InlineData(false)>]
let ``host-side pattern is cache-hit on rebuild after an unrelated change`` (useBuildKit: bool) =
    Assert.True(File.Exists(Path.Combine(fixtureSourceDir, "DiamondWithIncludedFilesSln.sln")))

    let sourceDir = copyFixtureToTempDir fixtureSourceDir
    let outputDir1 = freshTempDir ()
    let outputDir2 = freshTempDir ()
    let tag = $"dotnet-isolate-e2e-hostside-{Guid.NewGuid():N}"

    try
        Pipeline.isolate
            { ProjectPath = Path.Combine(sourceDir, "ServiceA", "ServiceA.csproj")
              OutputDir = Some outputDir1
              SolutionPath = None }
        |> ignore

        let exitCode1, output1 = runDockerBuild useBuildKit outputDir1 hostSideDockerfile tag
        Assert.True((exitCode1 = 0), $"first build failed:\n{output1}")

        touchUnrelatedFile (Path.Combine(sourceDir, "ServiceB", "Program.cs"))

        Pipeline.isolate
            { ProjectPath = Path.Combine(sourceDir, "ServiceA", "ServiceA.csproj")
              OutputDir = Some outputDir2
              SolutionPath = None }
        |> ignore

        // Sanity: we really did re-isolate into a fresh directory, not reuse the first one.
        Assert.NotEqual<string>(outputDir1, outputDir2)

        let exitCode2, output2 = runDockerBuild useBuildKit outputDir2 hostSideDockerfile tag
        Assert.True((exitCode2 = 0), $"second build failed:\n{output2}")

        Assert.True(
            expensiveLayersCached useBuildKit output2,
            $"expected restore/build/publish layers to cache-hit:\n{output2}"
        )
    finally
        removeImage tag
        deleteIfExists sourceDir
        deleteIfExists outputDir1
        deleteIfExists outputDir2
