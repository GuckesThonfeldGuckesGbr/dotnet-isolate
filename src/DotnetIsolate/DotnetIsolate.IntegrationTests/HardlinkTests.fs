module DotnetIsolate.IntegrationTests.HardlinkTests

open System.IO
open Xunit
open DotnetIsolate.Core
open DotnetIsolate.IntegrationTests.TestFixtures

[<Fact>]
let ``create makes destination a true hardlink, not a copy`` () =
    withTempDir (fun root ->
        Directory.CreateDirectory(root) |> ignore
        let source = Path.Combine(root, "source.txt")
        let destination = Path.Combine(root, "destination.txt")
        File.WriteAllText(source, "original")

        let result = Hardlink.create source destination

        Assert.Equal(Ok(), result)

        // A true hardlink shares the same underlying data - writing through either path is
        // visible via the other. A copy would not reflect this.
        File.WriteAllText(destination, "changed via destination")
        Assert.Equal("changed via destination", File.ReadAllText(source)))

[<Fact>]
let ``create returns an error when the source file does not exist`` () =
    withTempDir (fun root ->
        Directory.CreateDirectory(root) |> ignore
        let source = Path.Combine(root, "does-not-exist.txt")
        let destination = Path.Combine(root, "destination.txt")

        let result = Hardlink.create source destination

        Assert.True(Result.isError result))
