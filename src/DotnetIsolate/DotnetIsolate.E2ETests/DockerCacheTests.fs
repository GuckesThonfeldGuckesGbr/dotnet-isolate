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

let private twoPhaseDockerfile =
    Path.Combine(AppContext.BaseDirectory, "Fixtures", "TwoPhase", "Dockerfile")

let private toolProjectPath =
    Path.Combine(repoSrcDir, "DotnetIsolate", "DotnetIsolate", "DotnetIsolate.fsproj")

let private freshTempDir () =
    Path.Combine(Path.GetTempPath(), "dotnet-isolate-e2e", Path.GetRandomFileName())

/// QP-12: builds twice back to back, with an unrelated file changed in between, and asserts the
/// expensive restore/build/publish layers cache-hit on the second build - the DI-1 mechanism
/// (COPY --from=<stage> is cached by checksumming copied bytes, not by chaining off the source
/// stage's own layer history). BuildKit only - the project has decided to support buildx only, so
/// the classic-builder case is no longer exercised.
[<Fact>]
let ``self-contained pattern is cache-hit on rebuild after an unrelated change`` () =
    Assert.True(File.Exists(Path.Combine(fixtureSourceDir, "DiamondWithIncludedFilesSln.sln")))

    let contextDir = copyFixtureToTempDir fixtureSourceDir
    let tag = $"dotnet-isolate-e2e-selfcontained-{Guid.NewGuid():N}"

    try
        packLocalTool toolProjectPath (Path.Combine(contextDir, "nupkg"))

        let exitCode1, output1 = runDockerBuild true contextDir selfContainedDockerfile tag
        Assert.True((exitCode1 = 0), $"first build failed:\n{output1}")

        // Change a file outside ServiceA's dependency closure.
        touchUnrelatedFile (Path.Combine(contextDir, "ServiceB", "Program.cs"))

        let exitCode2, output2 = runDockerBuild true contextDir selfContainedDockerfile tag
        Assert.True((exitCode2 = 0), $"second build failed:\n{output2}")

        // Sanity: the unrelated change really did invalidate the early, uncacheable step -
        // otherwise the cache-hit assertion below would be vacuous.
        Assert.True(
            stepNotCached true output2 "dotnet isolate",
            $"expected the isolate step to rerun after an unrelated source change:\n{output2}"
        )

        // The actual DI-1 assertion: despite the isolate stage rerunning, the downstream
        // restore/build/publish layers still cache-hit.
        Assert.True(
            expensiveLayersCached true output2,
            $"expected restore/build/publish layers to cache-hit:\n{output2}"
        )
    finally
        removeImage tag
        deleteIfExists contextDir

/// Same QP-12 assertion for the host-side pattern: the harness calls Pipeline.isolate in-process
/// (rather than shelling out to the CLI) before each build, into a brand-new output directory
/// each time - REL-1 determinism means its content is byte-identical despite living at an
/// entirely different path, so COPY . /src and every downstream layer still cache-hit. BuildKit
/// only - the project has decided to support buildx only, so the classic-builder case is no
/// longer exercised.
[<Fact>]
let ``host-side pattern is cache-hit on rebuild after an unrelated change`` () =
    Assert.True(File.Exists(Path.Combine(fixtureSourceDir, "DiamondWithIncludedFilesSln.sln")))

    let sourceDir = copyFixtureToTempDir fixtureSourceDir
    let outputDir1 = freshTempDir ()
    let outputDir2 = freshTempDir ()
    let tag = $"dotnet-isolate-e2e-hostside-{Guid.NewGuid():N}"

    try
        Pipeline.isolate
            { ProjectPaths = [ Path.Combine(sourceDir, "ServiceA", "ServiceA.csproj") ]
              OutputDir = Some outputDir1
              SolutionPath = None
              RestoreOnly = false
              Clean = false }
        |> ignore

        let exitCode1, output1 = runDockerBuild true outputDir1 hostSideDockerfile tag
        Assert.True((exitCode1 = 0), $"first build failed:\n{output1}")

        touchUnrelatedFile (Path.Combine(sourceDir, "ServiceB", "Program.cs"))

        Pipeline.isolate
            { ProjectPaths = [ Path.Combine(sourceDir, "ServiceA", "ServiceA.csproj") ]
              OutputDir = Some outputDir2
              SolutionPath = None
              RestoreOnly = false
              Clean = false }
        |> ignore

        // Sanity: we really did re-isolate into a fresh directory, not reuse the first one.
        Assert.NotEqual<string>(outputDir1, outputDir2)

        let exitCode2, output2 = runDockerBuild true outputDir2 hostSideDockerfile tag
        Assert.True((exitCode2 = 0), $"second build failed:\n{output2}")

        Assert.True(
            expensiveLayersCached true output2,
            $"expected restore/build/publish layers to cache-hit:\n{output2}"
        )
    finally
        removeImage tag
        deleteIfExists sourceDir
        deleteIfExists outputDir1
        deleteIfExists outputDir2

/// A hand-written glob that sweeps the intermediate output tree into the resolved set - FR-15's
/// motivating case, and a common real-world pattern (copying config JSON alongside the binaries).
/// The SDK's own default excludes keep `obj/` out of the *implicit* globs, so without this the
/// artifact below is never a resolved input at all and the assertions become vacuous.
/// The bind-mounted isolate step of the TwoPhase Dockerfile, identified by its full command.
let private isolateStepNeedle =
    "dotnet isolate /src/ServiceA/ServiceA.csproj -o /isolated/full"

/// LF, matching the fixture it is spliced into - the repository is LF everywhere per .gitattributes.
let private artifactSweepingGlob =
    "  <ItemGroup>\n    <None Include=\"**\\*.json\" CopyToOutputDirectory=\"PreserveNewest\" />\n  </ItemGroup>\n\n"

/// Inserts `artifactSweepingGlob` into a copied fixture's project file, ahead of its first
/// ItemGroup. Mutates the temp copy only; the tracked fixture is shared with other suites.
let private addArtifactSweepingGlob (csprojPath: string) =
    let text = File.ReadAllText(csprojPath)
    let anchor = text.IndexOf("  <ItemGroup>")
    Assert.True((anchor >= 0), $"no ItemGroup to anchor the glob to in {csprojPath}")
    File.WriteAllText(csprojPath, text.Insert(anchor, artifactSweepingGlob))

/// FR-15 end to end, and the one claim in the design spec that was belief rather than test:
/// BuildKit does *not* narrow a `RUN --mount=type=bind` cache key by the process's actual read-set.
/// Changing a file the tool never opens - here a generated `obj/project.assets.json` - still
/// invalidates the isolate stage, because BuildKit digests the whole mounted subtree up front.
///
/// That is conceded and harmless, and this test pins both halves of why: the isolate stage reruns,
/// but the `COPY --from` boundary below it holds, because FR-15 drops the artifact from the
/// resolved set and so the isolated output's bytes are unchanged. Without FR-15 the glob above
/// would carry `project.assets.json` into the output and bust every downstream layer.
[<Fact>]
let ``a changed build artifact reruns the isolate stage but not the layers below it`` () =
    let contextDir = copyFixtureToTempDir fixtureSourceDir
    let tag = $"dotnet-isolate-e2e-artifact-{Guid.NewGuid():N}"
    let artifactPath = Path.Combine(contextDir, "ServiceA", "obj", "project.assets.json")

    try
        addArtifactSweepingGlob (Path.Combine(contextDir, "ServiceA", "ServiceA.csproj"))
        Directory.CreateDirectory(Path.GetDirectoryName(artifactPath)) |> ignore
        File.WriteAllText(artifactPath, $"{{ \"e2e\": \"{Guid.NewGuid():N}\" }}")

        packLocalTool toolProjectPath (Path.Combine(contextDir, "nupkg"))

        let exitCode1, output1 = runDockerBuild true contextDir twoPhaseDockerfile tag
        Assert.True((exitCode1 = 0), $"first build failed:\n{output1}")

        // Only the generated artifact changes - no source file is touched.
        File.WriteAllText(artifactPath, $"{{ \"e2e\": \"{Guid.NewGuid():N}\" }}")

        let exitCode2, output2 = runDockerBuild true contextDir twoPhaseDockerfile tag
        Assert.True((exitCode2 = 0), $"second build failed:\n{output2}")

        // Sanity: the glob really did sweep the artifact in and FR-15 really did drop it again -
        // otherwise the cache-hit assertion below would hold for the boring reason that the
        // artifact was never a resolved input in the first place.
        Assert.Contains("skipped 1 generated build artifact(s)", output2)

        // The previously unverified claim: the bind mount's cache key covers the whole mounted
        // subtree, not just what the tool read, so the isolate stage reruns regardless.
        // `stepNotCached` also answers true for a step it cannot find, so pin the needle first -
        // a Dockerfile edit must fail this test rather than silently hollow it out.
        Assert.Contains(isolateStepNeedle, output2)

        Assert.True(
            stepNotCached true output2 isolateStepNeedle,
            $"expected the bind-mounted isolate step to rerun after a build-artifact change:\n{output2}"
        )

        // FR-15's payoff: the output bytes are identical, so the boundary that matters holds.
        Assert.True(
            expensiveLayersCached true output2,
            $"expected restore/build/publish layers to cache-hit despite the artifact change:\n{output2}"
        )
    finally
        removeImage tag
        deleteIfExists contextDir

/// The property the whole --restore feature rests on: a change to a file *inside* the isolated
/// closure must still leave `dotnet restore` cache-hit, because the restore half of the output
/// contains no sources and is therefore byte-identical. The other tests here change a file
/// outside the closure, which leaves the entire output unchanged and so proves nothing about this.
[<Fact>]
let ``restore layer stays cached when a file inside the closure changes`` () =
    let contextDir = copyFixtureToTempDir fixtureSourceDir
    let tag = $"dotnet-isolate-e2e-twophase-{Guid.NewGuid():N}"

    try
        packLocalTool toolProjectPath (Path.Combine(contextDir, "nupkg"))

        let exitCode1, output1 = runDockerBuild true contextDir twoPhaseDockerfile tag
        Assert.True((exitCode1 = 0), $"first build failed:\n{output1}")

        // A source file *inside* ServiceA's closure - not an unrelated one.
        touchUnrelatedFile (Path.Combine(contextDir, "ServiceA", "Program.cs"))

        let exitCode2, output2 = runDockerBuild true contextDir twoPhaseDockerfile tag
        Assert.True((exitCode2 = 0), $"second build failed:\n{output2}")

        // Sanity: the change really did reach the build, so the assertion below isn't vacuous.
        Assert.True(
            stepNotCached true output2 "dotnet build -c Release",
            $"expected the build layer to rerun after a source change inside the closure:\n{output2}"
        )

        // The actual assertion: restore survived, because the restore half is unchanged.
        Assert.True(
            stepCached output2 "dotnet restore",
            $"expected the restore layer to stay cached after a source change inside the closure:\n{output2}"
        )
    finally
        removeImage tag
        deleteIfExists contextDir
