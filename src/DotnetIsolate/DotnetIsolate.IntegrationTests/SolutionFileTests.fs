module DotnetIsolate.IntegrationTests.SolutionFileTests

open System
open System.IO
open Xunit
open DotnetIsolate.Core
open DotnetIsolate.Core.SolutionFile
open DotnetIsolate.IntegrationTests.TestFixtures

/// Dogfoods filterSln against this repo's own real, `dotnet sln add`-generated .sln: scope it
/// down to just DotnetIsolate.Core, write the result out, and actually run `dotnet build`
/// against it - proving the generated file is genuinely valid, not just plausible-looking text.
[<Fact>]
let ``a filtered real .sln is a genuinely valid, buildable solution`` () =
    let repoSlnDir =
        // .../DotnetIsolate.IntegrationTests/bin/Debug/net8.0 -> .../DotnetIsolate
        Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../"))

    let sourceSlnPath = Path.Combine(repoSlnDir, "DotnetIsolate.sln")
    Assert.True(File.Exists(sourceSlnPath), $"expected to find {sourceSlnPath}")

    let coreProject =
        Path.GetFullPath(Path.Combine(repoSlnDir, "DotnetIsolate.Core", "DotnetIsolate.Core.fsproj"))

    let filtered =
        filterSln repoSlnDir (Set [ coreProject ]) (File.ReadAllText(sourceSlnPath))

    Assert.Contains("DotnetIsolate.Core", filtered)
    Assert.DoesNotContain("DotnetIsolate.UnitTests", filtered)
    Assert.DoesNotContain("DotnetIsolate.IntegrationTests", filtered)

    withTempDir (fun root ->
        Directory.CreateDirectory(root) |> ignore
        // The filtered .sln references DotnetIsolate.Core by its real relative path, so mirror
        // just that one project directory alongside the generated solution file.
        let sourceCoreDir = Path.Combine(repoSlnDir, "DotnetIsolate.Core")
        let destCoreDir = Path.Combine(root, "DotnetIsolate.Core")
        Directory.CreateDirectory(destCoreDir) |> ignore

        for file in Directory.GetFiles(sourceCoreDir) do
            File.Copy(file, Path.Combine(destCoreDir, Path.GetFileName(file)))

        let outputSlnPath = Path.Combine(root, "DotnetIsolate.sln")
        write outputSlnPath filtered

        let exitCode, stdout, stderr =
            runDotnet root [ "build"; outputSlnPath; "-nodeReuse:false" ]

        Assert.True((exitCode = 0), $"dotnet build failed (exit {exitCode}):\n{stdout}\n{stderr}"))
