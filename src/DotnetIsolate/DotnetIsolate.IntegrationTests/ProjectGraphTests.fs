module DotnetIsolate.IntegrationTests.ProjectGraphTests

open System.IO
open Xunit
open DotnetIsolate.Core
open DotnetIsolate.IntegrationTests.TestFixtures

/// A -> B, A -> C, B -> D, C -> D (mirrors the ServiceA/ServiceB/LogicCommon diamond shape
/// described in REQUIREMENTS.md QP-3, at a smaller scale for a fast, self-contained test).
[<Fact>]
let ``resolve follows real ProjectReferences and dedups a diamond dependency`` () =
    withTempDir (fun root ->
        writeProject (Path.Combine(root, "A")) "A" [ "B"; "C" ] []
        writeProject (Path.Combine(root, "B")) "B" [ "D" ] []
        writeProject (Path.Combine(root, "C")) "C" [ "D" ] []
        writeProject (Path.Combine(root, "D")) "D" [] []

        let entryProject = Path.Combine(root, "A", "A.fsproj")
        let result = ProjectGraph.resolve MsBuild.projectReferenceResolver entryProject

        let names =
            result |> List.map Path.GetFileNameWithoutExtension |> Set.ofList

        Assert.Equal<Set<string>>(Set [ "A"; "B"; "C"; "D" ], names)
        Assert.Equal(4, result.Length))
