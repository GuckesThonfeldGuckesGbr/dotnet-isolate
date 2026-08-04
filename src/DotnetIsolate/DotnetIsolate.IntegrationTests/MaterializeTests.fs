module DotnetIsolate.IntegrationTests.MaterializeTests

open System.IO
open Xunit
open DotnetIsolate.Core
open DotnetIsolate.IntegrationTests.TestFixtures

let private writeFile (path: string) (content: string) =
    Directory.CreateDirectory(Path.GetDirectoryName(path: string)) |> ignore
    File.WriteAllText(path, content)

[<Fact>]
let ``materialize mirrors nested relative paths and file content`` () =
    withTempDir (fun root ->
        let mirrorRoot = Path.Combine(root, "mirror")
        let outputRoot = Path.Combine(root, "output")

        writeFile (Path.Combine(mirrorRoot, "A", "A.fsproj")) "A project"
        writeFile (Path.Combine(mirrorRoot, "src", "Common", "Common.fsproj")) "Common project"

        let files =
            [ Path.Combine(mirrorRoot, "A", "A.fsproj")
              Path.Combine(mirrorRoot, "src", "Common", "Common.fsproj") ]

        Materialize.materialize LinkStrategy.Hardlink mirrorRoot outputRoot files

        Assert.Equal("A project", File.ReadAllText(Path.Combine(outputRoot, "A", "A.fsproj")))

        Assert.Equal(
            "Common project",
            File.ReadAllText(Path.Combine(outputRoot, "src", "Common", "Common.fsproj"))
        ))

[<Fact>]
let ``materialize hardlinks rather than copies when the strategy says Hardlink`` () =
    withTempDir (fun root ->
        let mirrorRoot = Path.Combine(root, "mirror")
        let outputRoot = Path.Combine(root, "output")
        let source = Path.Combine(mirrorRoot, "A.fsproj")
        writeFile source "original"

        Materialize.materialize LinkStrategy.Hardlink mirrorRoot outputRoot [ source ]

        // Same underlying data as the source - writing through the output path is visible via
        // the source. A copy would not reflect this.
        File.WriteAllText(Path.Combine(outputRoot, "A.fsproj"), "changed via output")
        Assert.Equal("changed via output", File.ReadAllText(source)))

[<Fact>]
let ``materialize deletes and recreates a pre-existing output folder (FR-7)`` () =
    withTempDir (fun root ->
        let mirrorRoot = Path.Combine(root, "mirror")
        let outputRoot = Path.Combine(root, "output")
        writeFile (Path.Combine(mirrorRoot, "A.fsproj")) "current"
        writeFile (Path.Combine(outputRoot, "stale-leftover.txt")) "from a previous run"

        Materialize.materialize LinkStrategy.Copy mirrorRoot outputRoot [ Path.Combine(mirrorRoot, "A.fsproj") ]

        Assert.False(File.Exists(Path.Combine(outputRoot, "stale-leftover.txt")))
        Assert.True(File.Exists(Path.Combine(outputRoot, "A.fsproj"))))
