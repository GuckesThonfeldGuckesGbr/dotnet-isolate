module DotnetIsolate.IntegrationTests.DeterminismTests

open System.IO
open System.Security.Cryptography
open Xunit
open DotnetIsolate.Core
open DotnetIsolate.IntegrationTests.TestFixtures

let private hashFile (path: string) =
    use sha256 = SHA256.Create()
    use stream = File.OpenRead(path)
    sha256.ComputeHash(stream) |> System.Convert.ToHexString

let private snapshot (dir: string) : Map<string, string> =
    Directory.GetFiles(dir, "*", SearchOption.AllDirectories)
    |> Array.map (fun f -> Path.GetRelativePath(dir, f), hashFile f)
    |> Map.ofArray

/// REL-1: running the tool twice on an unchanged input produces an identical output file set
/// with identical file content, so Docker layer caching stays valid. Runs isolate to two
/// separate output directories (rather than overwriting one) to isolate this from FR-7's
/// delete-and-recreate behavior, which is tested separately in MaterializeTests.
[<Fact>]
let ``isolate produces byte-identical output across two separate runs on the same input`` () =
    withTempDir (fun root ->
        writeProject (Path.Combine(root, "A")) "A" [ "B" ] [ "appsettings.json" ]
        writeProject (Path.Combine(root, "B")) "B" [] []
        writeSolution (Path.Combine(root, "Fixture.sln")) [ "A", "A/A.fsproj"; "B", "B/B.fsproj" ]

        let projectPath = Path.Combine(root, "A", "A.fsproj")
        let firstOutput = Path.Combine(root, "output1")
        let secondOutput = Path.Combine(root, "output2")

        Pipeline.isolate
            { ProjectPaths = [ projectPath ]
              OutputDir = Some firstOutput
              SolutionPath = None
              RestoreOnly = false
              Clean = false }
        |> ignore

        Pipeline.isolate
            { ProjectPaths = [ projectPath ]
              OutputDir = Some secondOutput
              SolutionPath = None
              RestoreOnly = false
              Clean = false }
        |> ignore

        let firstSnapshot = snapshot firstOutput
        let secondSnapshot = snapshot secondOutput

        Assert.Equal<Set<string>>(
            firstSnapshot |> Map.toList |> List.map fst |> Set.ofList,
            secondSnapshot |> Map.toList |> List.map fst |> Set.ofList
        )

        for KeyValue(relativePath, hash) in firstSnapshot do
            Assert.Equal(hash, secondSnapshot[relativePath]))
