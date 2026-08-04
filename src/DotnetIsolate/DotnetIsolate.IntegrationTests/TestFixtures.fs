module DotnetIsolate.IntegrationTests.TestFixtures

open System
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

    File.WriteAllText(Path.Combine(dir, "Program.fs"), $"printfn \"{name}\"")

    for f in contentFiles do
        File.WriteAllText(Path.Combine(dir, f), "{}")

/// Creates a fresh, uniquely-named temp directory, runs `test` against it, and always deletes it
/// afterward - even if `test` throws.
let withTempDir (test: string -> unit) =
    let root = Path.Combine(Path.GetTempPath(), "dotnet-isolate-tests", Path.GetRandomFileName())

    try
        test root
    finally
        if Directory.Exists(root) then
            Directory.Delete(root, recursive = true)
