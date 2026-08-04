module DotnetIsolate.IntegrationTests.LinkStrategyTests

open System.IO
open Xunit
open DotnetIsolate.Core
open DotnetIsolate.IntegrationTests.TestFixtures

[<Fact>]
let ``probe picks Hardlink on a filesystem that supports it, and cleans up after itself`` () =
    withTempDir (fun root ->
        let sourceDir = Path.Combine(root, "source")
        let destinationDir = Path.Combine(root, "destination")
        Directory.CreateDirectory(sourceDir) |> ignore
        let sampleFile = Path.Combine(sourceDir, "sample.txt")
        File.WriteAllText(sampleFile, "content")

        let strategy = LinkStrategy.probe sampleFile destinationDir

        // This sandbox's temp filesystem supports hardlinks, so the probe should find that out.
        Assert.Equal(LinkStrategy.Hardlink, strategy)

        // The probe file must not be left behind in the destination.
        Assert.Empty(Directory.GetFiles(destinationDir)))
