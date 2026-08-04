module DotnetIsolate.IntegrationTests.ProjectGraphTests

open System.IO
open Xunit
open DotnetIsolate.Core

let private writeProject (dir: string) (name: string) (references: string list) =
    Directory.CreateDirectory(dir) |> ignore

    let referenceItems =
        references
        |> List.map (fun r -> $"    <ProjectReference Include=\"../{r}/{r}.fsproj\"/>")
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
</Project>
"""
    )

    File.WriteAllText(Path.Combine(dir, "Program.fs"), $"printfn \"{name}\"")

/// A -> B, A -> C, B -> D, C -> D (mirrors the ServiceA/ServiceB/LogicCommon diamond shape
/// described in REQUIREMENTS.md QP-3, at a smaller scale for a fast, self-contained test).
[<Fact>]
let ``resolve follows real ProjectReferences and dedups a diamond dependency`` () =
    let root = Path.Combine(Path.GetTempPath(), "dotnet-isolate-tests", Path.GetRandomFileName())

    try
        writeProject (Path.Combine(root, "A")) "A" [ "B"; "C" ]
        writeProject (Path.Combine(root, "B")) "B" [ "D" ]
        writeProject (Path.Combine(root, "C")) "C" [ "D" ]
        writeProject (Path.Combine(root, "D")) "D" []

        let entryProject = Path.Combine(root, "A", "A.fsproj")
        let result = ProjectGraph.resolve MsBuild.projectReferenceResolver entryProject

        let names =
            result |> List.map Path.GetFileNameWithoutExtension |> Set.ofList

        Assert.Equal<Set<string>>(Set [ "A"; "B"; "C"; "D" ], names)
        Assert.Equal(4, result.Length)
    finally
        if Directory.Exists(root) then
            Directory.Delete(root, recursive = true)
