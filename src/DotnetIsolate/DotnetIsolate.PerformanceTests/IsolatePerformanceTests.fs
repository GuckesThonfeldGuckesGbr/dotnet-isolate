module DotnetIsolate.PerformanceTests.IsolatePerformanceTests

open System
open System.Diagnostics
open System.IO
open Xunit
open DotnetIsolate.Core

/// Not a correctness test, and deliberately asserts no hard wall-clock threshold - CI/dev machine
/// speed varies too much for a reliable gate, and a flaky perf test is worse than none. Instead it
/// writes the graph size and timing to BenchmarkResults.write's output so CI can chart the trend
/// over time (see .github/workflows/build.yml's `performance` job) and a regression (e.g. the
/// MSBuild-spawn-count fix regressing back to one process per project per pipeline step) is
/// visible across commits, not just by eye in one run's console output.
[<Fact>]
let ``isolating a project out of OrchardCore's real 240+-project graph completes and reports timing`` () =
    let repoRoot = OrchardCoreFixture.clone ()
    let outputDir = Path.Combine(Path.GetTempPath(), "dotnet-isolate-perf-output-" + Guid.NewGuid().ToString("N"))

    try
        let targetProject =
            Path.Combine(repoRoot, "src", "OrchardCore.Cms.Web", "OrchardCore.Cms.Web.csproj")

        Assert.True(File.Exists(targetProject), $"expected to find {targetProject}")

        // Done once here, outside the timed section below, so restore/build time isn't mistaken
        // for dotnet-isolate's own.
        OrchardCoreFixture.build repoRoot targetProject

        let stopwatch = Stopwatch.StartNew()

        let result =
            Pipeline.isolate
                { ProjectPath = targetProject
                  OutputDir = Some outputDir
                  SolutionPath = None }

        stopwatch.Stop()

        Assert.True(Directory.Exists(outputDir))

        Assert.True(
            result.IncludedProjects.Length > 50,
            $"expected a large project graph out of OrchardCore, got only {result.IncludedProjects.Length}"
        )

        printfn
            "OrchardCore.Cms.Web isolate: %d projects, %d files, %dms"
            result.IncludedProjects.Length
            result.FileCount
            stopwatch.ElapsedMilliseconds

        BenchmarkResults.write
            [ { Name = "OrchardCore.Cms.Web isolate: wall-clock time"
                Unit = "ms"
                Value = float stopwatch.ElapsedMilliseconds }
              { Name = "OrchardCore.Cms.Web isolate: included projects"
                Unit = "projects"
                Value = float result.IncludedProjects.Length }
              { Name = "OrchardCore.Cms.Web isolate: included files"
                Unit = "files"
                Value = float result.FileCount } ]
    finally
        Directory.Delete(repoRoot, recursive = true)

        if Directory.Exists(outputDir) then
            Directory.Delete(outputDir, recursive = true)
