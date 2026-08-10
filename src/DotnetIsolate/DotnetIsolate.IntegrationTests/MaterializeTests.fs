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

        Materialize.materialize LinkStrategy.Hardlink false mirrorRoot outputRoot files |> ignore

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

        Materialize.materialize LinkStrategy.Hardlink false mirrorRoot outputRoot [ source ] |> ignore

        // Same underlying data as the source - writing through the output path is visible via
        // the source. A copy would not reflect this.
        File.WriteAllText(Path.Combine(outputRoot, "A.fsproj"), "changed via output")
        Assert.Equal("changed via output", File.ReadAllText(source)))

// Supersedes the old FR-7 delete-and-recreate default: an unrelated pre-existing file survives,
// because the destructive default could (and did) delete a user's source tree.
[<Fact>]
let ``materialize preserves pre-existing output content and reports it as stale`` () =
    withTempDir (fun root ->
        let mirrorRoot = Path.Combine(root, "mirror")
        let outputRoot = Path.Combine(root, "output")
        writeFile (Path.Combine(mirrorRoot, "A.fsproj")) "current"
        writeFile (Path.Combine(outputRoot, "stale-leftover.txt")) "from a previous run"

        let stale =
            Materialize.materialize LinkStrategy.Copy false mirrorRoot outputRoot [ Path.Combine(mirrorRoot, "A.fsproj") ]

        Assert.True(File.Exists(Path.Combine(outputRoot, "stale-leftover.txt")))
        Assert.True(File.Exists(Path.Combine(outputRoot, "A.fsproj")))
        Assert.Equal(1, stale.Length)
        Assert.Equal("stale-leftover.txt", Path.GetFileName(List.head stale)))

[<Fact>]
let ``materialize does not report a file this run produced as stale`` () =
    withTempDir (fun root ->
        let mirrorRoot = Path.Combine(root, "mirror")
        let outputRoot = Path.Combine(root, "output")
        writeFile (Path.Combine(mirrorRoot, "A.fsproj")) "current"
        // Same relative path this run will write, left over from a previous run.
        writeFile (Path.Combine(outputRoot, "A.fsproj")) "previous"

        let stale =
            Materialize.materialize LinkStrategy.Copy false mirrorRoot outputRoot [ Path.Combine(mirrorRoot, "A.fsproj") ]

        Assert.Empty(stale)
        Assert.Equal("current", File.ReadAllText(Path.Combine(outputRoot, "A.fsproj"))))

// Regression guard for the Hardlink/EEXIST bug: creating a hardlink at a path that already has an
// entry fails, unlike File.Copy(overwrite = true), so merging (the default since Task 3) into an
// output directory the strategy already populated must remove the stale entry first. Only
// LinkStrategy.probe decides Hardlink vs Copy for a real run, so this is the only place that
// exercises the merge-over-hardlink path independent of the filesystem the tests happen to run on.
[<Fact>]
let ``materialize succeeds when re-run with Hardlink over its own prior output`` () =
    withTempDir (fun root ->
        let mirrorRoot = Path.Combine(root, "mirror")
        let outputRoot = Path.Combine(root, "output")
        let source = Path.Combine(mirrorRoot, "A.fsproj")
        writeFile source "current"

        Materialize.materialize LinkStrategy.Hardlink false mirrorRoot outputRoot [ source ] |> ignore
        Materialize.materialize LinkStrategy.Hardlink false mirrorRoot outputRoot [ source ] |> ignore

        Assert.Equal("current", File.ReadAllText(Path.Combine(outputRoot, "A.fsproj"))))

[<Fact>]
let ``materialize with clean empties the output folder first`` () =
    withTempDir (fun root ->
        let mirrorRoot = Path.Combine(root, "mirror")
        let outputRoot = Path.Combine(root, "output")
        writeFile (Path.Combine(mirrorRoot, "A.fsproj")) "current"
        writeFile (Path.Combine(outputRoot, "stale-leftover.txt")) "from a previous run"

        let stale =
            Materialize.materialize LinkStrategy.Copy true mirrorRoot outputRoot [ Path.Combine(mirrorRoot, "A.fsproj") ]

        Assert.False(File.Exists(Path.Combine(outputRoot, "stale-leftover.txt")))
        Assert.True(File.Exists(Path.Combine(outputRoot, "A.fsproj")))
        Assert.Empty(stale))
