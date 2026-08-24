module DotnetIsolate.IntegrationTests.CliTests

open System
open System.IO
open Xunit
open DotnetIsolate.IntegrationTests.TestFixtures

/// .../DotnetIsolate.IntegrationTests/bin/Release/net8.0 -> .../src
let private repoSrcDir =
    Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../"))

let private cliProjectPath =
    Path.Combine(repoSrcDir, "DotnetIsolate", "DotnetIsolate", "DotnetIsolate.fsproj")

/// FR-17's whole point: a CI script pipes list-files' stdout straight into a diff, so nothing but
/// the file list itself may ever appear there. Exercises the real built CLI (not Pipeline.listFiles
/// directly) because this property lives entirely in Program.fs's runListFiles, which has no other
/// test coverage - the E2E suite only exercises the materialize verb.
[<Fact>]
let ``list-files prints only absolute existing paths to stdout, nothing else`` () =
    withTempDir (fun root ->
        writeProject (Path.Combine(root, "A")) "A" [] []
        writeSolution (Path.Combine(root, "Fixture.sln")) [ "A", "A/A.fsproj" ]

        let exitCode, stdout, stderr =
            runDotnet
                root
                [ "run"
                  "--project"
                  cliProjectPath
                  "-c"
                  "Release"
                  "--no-build"
                  "--no-restore"
                  "--"
                  "list-files"
                  Path.Combine(root, "A", "A.fsproj") ]

        Assert.True((exitCode = 0), $"list-files failed (exit {exitCode}):\n{stdout}\n{stderr}")

        let lines =
            stdout.Split('\n')
            |> Array.map (fun l -> l.TrimEnd('\r'))
            |> Array.filter (fun l -> l <> "")

        Assert.NotEmpty(lines)

        for line in lines do
            Assert.True(Path.IsPathRooted(line), $"expected an absolute path, got: {line}")
            Assert.True(File.Exists(line), $"expected an existing file, got: {line}")

        // Diagnostics belong on stderr, never stdout - the property a CI script depends on.
        Assert.DoesNotContain("Using solution", stdout)
        Assert.Contains("Using solution", stderr))
