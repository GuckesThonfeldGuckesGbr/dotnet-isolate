module DotnetIsolate.IntegrationTests.DiamondWithIncludedFilesTests

open System
open System.IO
open Xunit
open DotnetIsolate.Core
open DotnetIsolate.IntegrationTests.TestFixtures

/// .../DotnetIsolate.IntegrationTests/bin/Release/net8.0 -> .../src
let private repoSrcDir =
    Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../"))

let private slnFixtureRoot =
    Path.Combine(repoSrcDir, "TestSolutions", "DiamondWithIncludedFilesSln")

let private slnxFixtureRoot =
    Path.Combine(repoSrcDir, "TestSolutions", "DiamondWithIncludedFiles")

/// The .slnx fixture's projects target net10.0 (DESIGN.md's .slnx SDK caveat: an SDK below .NET
/// 10 fails outright with MSB4068 before isolation is even relevant). Neither fixture directory
/// has its own global.json, so `dotnet`'s SDK resolution there picks whatever's installed and
/// newest - checking the installed SDK list is enough to predict whether building it will work.
let private hasNet10Sdk () =
    let exitCode, stdout, _ = runDotnet repoSrcDir [ "--list-sdks" ]

    exitCode = 0
    && stdout.Split('\n')
       |> Array.exists (fun line -> line.TrimStart().StartsWith("10."))

[<Fact>]
let ``isolating ServiceA from the .sln fixture includes only its dependency closure`` () =
    Assert.True(File.Exists(Path.Combine(slnFixtureRoot, "DiamondWithIncludedFilesSln.sln")))

    withTempDir (fun outputDir ->
        let result =
            Pipeline.isolate
                { ProjectPaths = [ Path.Combine(slnFixtureRoot, "ServiceA", "ServiceA.csproj") ]
                  OutputDir = Some outputDir
                  SolutionPath = None
                  RestoreOnly = false
                  Clean = false }

        Assert.True(File.Exists(Path.Combine(outputDir, "ServiceA", "ServiceA.csproj")))
        Assert.True(File.Exists(Path.Combine(outputDir, "LogicA", "LogicA.csproj")))
        Assert.True(File.Exists(Path.Combine(outputDir, "LogicCommon", "LogicCommon.csproj")))
        Assert.True(File.Exists(Path.Combine(outputDir, "LogicCommon", "credentials.json")))

        Assert.False(Directory.Exists(Path.Combine(outputDir, "ServiceB")))
        Assert.False(Directory.Exists(Path.Combine(outputDir, "LogicB")))

        // The core assertion (QP-3): a file that exists on disk but isn't referenced by any
        // MSBuild item (.env.local, explicitly <None Remove>'d in the fixture) must not be
        // copied, even though its containing project (LogicCommon) is included.
        Assert.False(File.Exists(Path.Combine(outputDir, "LogicCommon", ".env.local")))

        Assert.True(result.SolutionRoot.IsSome)
        let outputSln = Path.Combine(outputDir, "DiamondWithIncludedFilesSln.sln")
        Assert.True(File.Exists(outputSln))

        let exitCode, stdout, stderr =
            // -maxcpucount:1: MSBuild's default parallel solution build can occasionally race
            // writing a shared dependency's deps.json (GenerateDepsFile IOException) when it's
            // referenced by two other projects being built concurrently - this fixture's diamond
            // shape (ServiceA/ServiceB both -> LogicCommon) hits exactly that shape.
            runDotnet outputDir [ "build"; outputSln; "-nodeReuse:false"; "-maxcpucount:1" ]

        Assert.True((exitCode = 0), $"dotnet build failed (exit {exitCode}):\n{stdout}\n{stderr}"))

[<Fact>]
let ``isolating ServiceB from the .sln fixture includes only its dependency closure`` () =
    Assert.True(File.Exists(Path.Combine(slnFixtureRoot, "DiamondWithIncludedFilesSln.sln")))

    withTempDir (fun outputDir ->
        let result =
            Pipeline.isolate
                { ProjectPaths = [ Path.Combine(slnFixtureRoot, "ServiceB", "ServiceB.csproj") ]
                  OutputDir = Some outputDir
                  SolutionPath = None
                  RestoreOnly = false
                  Clean = false }

        Assert.True(File.Exists(Path.Combine(outputDir, "ServiceB", "ServiceB.csproj")))
        Assert.True(File.Exists(Path.Combine(outputDir, "LogicB", "LogicB.csproj")))
        Assert.True(File.Exists(Path.Combine(outputDir, "LogicCommon", "LogicCommon.csproj")))
        Assert.True(File.Exists(Path.Combine(outputDir, "LogicCommon", "credentials.json")))

        Assert.False(Directory.Exists(Path.Combine(outputDir, "ServiceA")))
        Assert.False(Directory.Exists(Path.Combine(outputDir, "LogicA")))
        Assert.False(File.Exists(Path.Combine(outputDir, "LogicCommon", ".env.local")))

        Assert.True(result.SolutionRoot.IsSome)
        let outputSln = Path.Combine(outputDir, "DiamondWithIncludedFilesSln.sln")
        Assert.True(File.Exists(outputSln))

        let exitCode, stdout, stderr =
            // -maxcpucount:1: MSBuild's default parallel solution build can occasionally race
            // writing a shared dependency's deps.json (GenerateDepsFile IOException) when it's
            // referenced by two other projects being built concurrently - this fixture's diamond
            // shape (ServiceA/ServiceB both -> LogicCommon) hits exactly that shape.
            runDotnet outputDir [ "build"; outputSln; "-nodeReuse:false"; "-maxcpucount:1" ]

        Assert.True((exitCode = 0), $"dotnet build failed (exit {exitCode}):\n{stdout}\n{stderr}"))

[<SkippableFact>]
let ``isolating ServiceA from the .slnx fixture includes only its dependency closure`` () =
    Skip.IfNot(hasNet10Sdk (), "requires a .NET 10 SDK to build the .slnx fixture's net10.0 projects")

    Assert.True(File.Exists(Path.Combine(slnxFixtureRoot, "DiamondWithIncludedFiles.slnx")))

    withTempDir (fun outputDir ->
        let result =
            Pipeline.isolate
                { ProjectPaths = [ Path.Combine(slnxFixtureRoot, "ServiceA", "ServiceA.csproj") ]
                  OutputDir = Some outputDir
                  SolutionPath = None
                  RestoreOnly = false
                  Clean = false }

        Assert.True(File.Exists(Path.Combine(outputDir, "ServiceA", "ServiceA.csproj")))
        Assert.True(File.Exists(Path.Combine(outputDir, "LogicA", "LogicA.csproj")))
        Assert.True(File.Exists(Path.Combine(outputDir, "LogicCommon", "LogicCommon.csproj")))
        Assert.True(File.Exists(Path.Combine(outputDir, "LogicCommon", "credentials.json")))

        Assert.False(Directory.Exists(Path.Combine(outputDir, "ServiceB")))
        Assert.False(Directory.Exists(Path.Combine(outputDir, "LogicB")))
        Assert.False(File.Exists(Path.Combine(outputDir, "LogicCommon", ".env.local")))

        Assert.True(result.SolutionRoot.IsSome)
        let outputSlnx = Path.Combine(outputDir, "DiamondWithIncludedFiles.slnx")
        Assert.True(File.Exists(outputSlnx))

        let exitCode, stdout, stderr =
            runDotnet outputDir [ "build"; outputSlnx; "-nodeReuse:false" ]

        Assert.True((exitCode = 0), $"dotnet build failed (exit {exitCode}):\n{stdout}\n{stderr}"))

[<SkippableFact>]
let ``isolating ServiceB from the .slnx fixture includes only its dependency closure`` () =
    Skip.IfNot(hasNet10Sdk (), "requires a .NET 10 SDK to build the .slnx fixture's net10.0 projects")

    Assert.True(File.Exists(Path.Combine(slnxFixtureRoot, "DiamondWithIncludedFiles.slnx")))

    withTempDir (fun outputDir ->
        let result =
            Pipeline.isolate
                { ProjectPaths = [ Path.Combine(slnxFixtureRoot, "ServiceB", "ServiceB.csproj") ]
                  OutputDir = Some outputDir
                  SolutionPath = None
                  RestoreOnly = false
                  Clean = false }

        Assert.True(File.Exists(Path.Combine(outputDir, "ServiceB", "ServiceB.csproj")))
        Assert.True(File.Exists(Path.Combine(outputDir, "LogicB", "LogicB.csproj")))
        Assert.True(File.Exists(Path.Combine(outputDir, "LogicCommon", "LogicCommon.csproj")))
        Assert.True(File.Exists(Path.Combine(outputDir, "LogicCommon", "credentials.json")))

        Assert.False(Directory.Exists(Path.Combine(outputDir, "ServiceA")))
        Assert.False(Directory.Exists(Path.Combine(outputDir, "LogicA")))
        Assert.False(File.Exists(Path.Combine(outputDir, "LogicCommon", ".env.local")))

        Assert.True(result.SolutionRoot.IsSome)
        let outputSlnx = Path.Combine(outputDir, "DiamondWithIncludedFiles.slnx")
        Assert.True(File.Exists(outputSlnx))

        let exitCode, stdout, stderr =
            runDotnet outputDir [ "build"; outputSlnx; "-nodeReuse:false" ]

        Assert.True((exitCode = 0), $"dotnet build failed (exit {exitCode}):\n{stdout}\n{stderr}"))
