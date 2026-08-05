module DotnetIsolate.IntegrationTests.TestFixtures

open System
open System.Diagnostics
open System.IO

/// Writes a minimal net8.0 F# project named `name` under `dir`, referencing `references`
/// (other project names expected to be sibling directories) and any extra content files.
let writeProject (dir: string) (name: string) (references: string list) (contentFiles: string list) =
    Directory.CreateDirectory(dir) |> ignore

    let referenceItems =
        references
        |> List.map (fun r -> $"    <ProjectReference Include=\"../{r}/{r}.fsproj\"/>")
        |> String.concat "\n"

    let contentItems =
        contentFiles
        |> List.map (fun f -> $"    <Content Include=\"{f}\" CopyToOutputDirectory=\"PreserveNewest\"/>")
        |> String.concat "\n"

    File.WriteAllText(
        Path.Combine(dir, $"{name}.fsproj"),
        $"""<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net8.0</TargetFramework>
  </PropertyGroup>
  <ItemGroup>
    <Compile Include="Program.fs"/>
  </ItemGroup>
  <ItemGroup>
{referenceItems}
  </ItemGroup>
  <ItemGroup>
{contentItems}
  </ItemGroup>
</Project>
"""
    )

    // A module declaration is required regardless of OutputType - the "last file may omit it"
    // exception only applies to Exe apps, and these fixtures default to Library (no OutputType
    // set), same as any real referenced-but-not-run project in a solution.
    File.WriteAllText(Path.Combine(dir, "Program.fs"), $"module {name}.Program\n\nprintfn \"{name}\"")

    for f in contentFiles do
        File.WriteAllText(Path.Combine(dir, f), "{}")

let private fsharpProjectTypeGuid = "F2A71F9B-5D33-465A-A702-920D77279786"

/// Writes a minimal, real classic .sln at `path` referencing `projects` (name, path-relative-to-
/// the-solution-file pairs), matching the structure `dotnet sln add` actually produces (see
/// SolutionFile.fs) - random GUIDs, one ProjectConfigurationPlatforms entry per project.
let writeSolution (path: string) (projects: (string * string) list) =
    let withGuids =
        projects
        |> List.map (fun (name, relPath) -> name, relPath, Guid.NewGuid().ToString("D").ToUpperInvariant())

    let projectLines =
        withGuids
        |> List.map (fun (name, relPath, guid) ->
            $"Project(\"{{{fsharpProjectTypeGuid}}}\") = \"{name}\", \"{relPath}\", \"{{{guid}}}\"\r\nEndProject")
        |> String.concat "\r\n"

    let configLines =
        withGuids
        |> List.map (fun (_, _, guid) ->
            $"\t\t{{{guid}}}.Debug|Any CPU.ActiveCfg = Debug|Any CPU\r\n\t\t{{{guid}}}.Debug|Any CPU.Build.0 = Debug|Any CPU")
        |> String.concat "\r\n"

    let content =
        "\r\nMicrosoft Visual Studio Solution File, Format Version 12.00\r\n"
        + "# Visual Studio Version 17\r\n"
        + $"{projectLines}\r\n"
        + "Global\r\n"
        + "\tGlobalSection(SolutionConfigurationPlatforms) = preSolution\r\n"
        + "\t\tDebug|Any CPU = Debug|Any CPU\r\n"
        + "\tEndGlobalSection\r\n"
        + "\tGlobalSection(ProjectConfigurationPlatforms) = postSolution\r\n"
        + $"{configLines}\r\n"
        + "\tEndGlobalSection\r\n"
        + "EndGlobal\r\n"

    File.WriteAllText(path, content, Text.UTF8Encoding(true))

/// Environment variables the outer `dotnet test` muxer sets (resolved via this repo's own
/// global.json, which pins the SDK to 8.0.0) that a child `dotnet` process would otherwise
/// inherit and be pinned by, even when its own target (e.g. a net10.0 fixture) needs a different
/// SDK. Stripped so each subprocess resolves its SDK independently from its own working
/// directory - found the hard way: a .slnx/net10.0 fixture build failed with NETSDK1045 ("current
/// SDK does not support net10.0") using SDK 8.0.423, despite 10.0.x being installed and a plain
/// shell `dotnet build` from the same directory picking it correctly.
let private inheritedSdkPinningEnvVars =
    [ "MSBuildSDKsPath"
      "MSBUILD_EXE_PATH"
      "MSBuildExtensionsPath"
      "DOTNET_MSBUILD_SDK_RESOLVER_SDKS_DIR"
      "DOTNET_MSBUILD_SDK_RESOLVER_CLI_DIR"
      "DOTNET_HOST_PATH" ]

/// Shells out to `dotnet <args>` in `workingDir` and returns (exit code, stdout, stderr). These
/// tests spawn real dotnet/MSBuild subprocesses to prove output is genuinely buildable, not just
/// plausible-looking text.
let runDotnet (workingDir: string) (args: string list) =
    let psi =
        ProcessStartInfo(
            "dotnet",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            WorkingDirectory = workingDir
        )

    for var in inheritedSdkPinningEnvVars do
        psi.Environment.Remove(var) |> ignore

    args |> List.iter psi.ArgumentList.Add
    use proc = Process.Start(psi)
    let stdout = proc.StandardOutput.ReadToEnd()
    let stderr = proc.StandardError.ReadToEnd()
    proc.WaitForExit()
    proc.ExitCode, stdout, stderr

/// Creates a fresh, uniquely-named temp directory, runs `test` against it, and always deletes it
/// afterward - even if `test` throws.
let withTempDir (test: string -> unit) =
    let root = Path.Combine(Path.GetTempPath(), "dotnet-isolate-tests", Path.GetRandomFileName())

    try
        test root
    finally
        if Directory.Exists(root) then
            Directory.Delete(root, recursive = true)
