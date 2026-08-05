/// Clones a real, large, actively-maintained .NET solution (OrchardCMS/OrchardCore - 241 projects
/// across a genuine modular-CMS dependency graph, not a synthetic fixture) to exercise
/// dotnet-isolate's performance against something representative of what it's actually for:
/// shrinking a large solution's Docker build context. Pinned to a specific commit so the
/// performance test is reproducible instead of drifting with upstream - bump `pinnedCommit` (and
/// re-verify the isolate still succeeds) if it needs updating.
module DotnetIsolate.PerformanceTests.OrchardCoreFixture

open System
open System.IO
open DotnetIsolate.Core

let private repoUrl = "https://github.com/OrchardCMS/OrchardCore.git"

/// https://github.com/OrchardCMS/OrchardCore/commit/2f75eacf504ed94a332e4c252bb2236bca0f78bc
/// (main, 2026-08-05).
let pinnedCommit = "2f75eacf504ed94a332e4c252bb2236bca0f78bc"

let private runGit (workingDir: string) (args: string list) =
    let psi = Diagnostics.ProcessStartInfo("git", WorkingDirectory = workingDir)
    args |> List.iter psi.ArgumentList.Add
    let _, stderr, exitCode = MsBuild.runCapturingOutput psi

    if exitCode <> 0 then
        failwith $"""git {String.concat " " args} failed (exit {exitCode}): {stderr}"""

/// Same discovery as TestFixtures.fs's `runDotnet` in the IntegrationTests project: `dotnet
/// test`'s host process pins MSBuildSDKsPath/MSBuildExtensionsPath/etc. to ITS OWN SDK (this
/// repo's, 8.0), and a child `dotnet` process would otherwise inherit that pin and be forced onto
/// the wrong SDK - verified directly, `dotnet build` against OrchardCore (net10.0, its own
/// global.json) failed with NETSDK1045 until these were stripped, despite a plain shell invocation
/// from the same directory picking net10.0 correctly.
let private inheritedSdkPinningEnvVars =
    [ "MSBuildSDKsPath"
      "MSBUILD_EXE_PATH"
      "MSBuildExtensionsPath"
      "DOTNET_MSBUILD_SDK_RESOLVER_SDKS_DIR"
      "DOTNET_MSBUILD_SDK_RESOLVER_CLI_DIR"
      "DOTNET_HOST_PATH" ]

/// Shallow-clones OrchardCore at `pinnedCommit` into a fresh temp directory and returns its path.
/// Fetches the pinned commit directly (rather than `git clone --depth 1`, which only ever gets
/// the default branch's current tip) so the checkout is the exact commit every run, regardless of
/// what's since landed on OrchardCore's main.
let clone () : string =
    let dir = Path.Combine(Path.GetTempPath(), "dotnet-isolate-perf-" + Guid.NewGuid().ToString("N"))
    Directory.CreateDirectory(dir) |> ignore

    runGit dir [ "init"; "--quiet" ]
    runGit dir [ "remote"; "add"; "origin"; repoUrl ]
    runGit dir [ "fetch"; "--quiet"; "--depth"; "1"; "origin"; pinnedCommit ]
    runGit dir [ "checkout"; "--quiet"; "FETCH_HEAD" ]

    dir

/// Builds `targetProject` (and transitively its ProjectReference closure) for real. Some of
/// OrchardCore's modules reference another project's compiled output DLL directly as a build
/// item (a source-generator's assembly, referenced by path rather than a proper analyzer
/// ProjectReference) - a plain `dotnet restore` leaves that file missing, and dotnet-isolate would
/// then fail to mirror it since the pipeline expects every resolved item to genuinely exist.
let build (repoRoot: string) (targetProject: string) : unit =
    let psi = Diagnostics.ProcessStartInfo("dotnet", WorkingDirectory = repoRoot)
    psi.ArgumentList.Add("build")
    psi.ArgumentList.Add(targetProject)
    psi.ArgumentList.Add("-nodeReuse:false")

    for var in inheritedSdkPinningEnvVars do
        psi.Environment.Remove(var) |> ignore

    let stdout, stderr, exitCode = MsBuild.runCapturingOutput psi

    if exitCode <> 0 then
        failwith $"dotnet build failed (exit {exitCode}):\n{stdout}\n{stderr}"
