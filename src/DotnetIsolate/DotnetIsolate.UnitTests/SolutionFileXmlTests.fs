module DotnetIsolate.UnitTests.SolutionFileXmlTests

open System.IO
open Xunit
open DotnetIsolate.Core.SolutionFileXml

// Computed the same way filterSlnx itself resolves a declared Path attribute (Path.Combine +
// GetFullPath against solutionDir "/repo"), rather than a hardcoded absolute-path literal - see
// SolutionFileTests.fs's `includedA` for why a literal doesn't match on Windows.
let private includedA = Path.GetFullPath(Path.Combine("/repo", "A", "A.fsproj"))

let private sampleSlnx =
    "<Solution>\n\
  <Project Path=\"A/A.fsproj\" />\n\
  <Project Path=\"B/B.fsproj\" />\n\
</Solution>"

[<Fact>]
let ``keeps only project entries whose resolved path is included`` () =
    let result = filterSlnx "/repo" (Set [ includedA ]) sampleSlnx

    Assert.Contains("A/A.fsproj", result)
    Assert.DoesNotContain("B/B.fsproj", result)

[<Fact>]
let ``a folder left with no included projects is dropped entirely`` () =
    let slnxWithFolder =
        "<Solution>\n\
  <Folder Name=\"/Docs/\">\n\
    <Project Path=\"B/B.fsproj\" />\n\
  </Folder>\n\
</Solution>"

    let result = filterSlnx "/repo" (Set [ includedA ]) slnxWithFolder

    Assert.DoesNotContain("Docs", result)
    Assert.DoesNotContain("B/B.fsproj", result)

[<Fact>]
let ``a folder retaining at least one included project is kept, and its excluded sibling is not`` () =
    let slnxWithFolder =
        "<Solution>\n\
  <Folder Name=\"/Src/\">\n\
    <Project Path=\"A/A.fsproj\" />\n\
    <Project Path=\"B/B.fsproj\" />\n\
  </Folder>\n\
</Solution>"

    let result = filterSlnx "/repo" (Set [ includedA ]) slnxWithFolder

    Assert.Contains("Src", result)
    Assert.Contains("A/A.fsproj", result)
    Assert.DoesNotContain("B/B.fsproj", result)

[<Fact>]
let ``backslash-separated declared paths resolve the same as forward-slash ones`` () =
    let slnxWithBackslash =
        "<Solution>\n\
  <Project Path=\"A\\A.fsproj\" />\n\
  <Project Path=\"B\\B.fsproj\" />\n\
</Solution>"

    let result = filterSlnx "/repo" (Set [ includedA ]) slnxWithBackslash

    Assert.Contains("A\\A.fsproj", result)
    Assert.DoesNotContain("B\\B.fsproj", result)

[<Fact>]
let ``a solution with no projects at all is handled without crashing`` () =
    let empty = "<Solution>\n</Solution>"

    let result = filterSlnx "/repo" (Set []) empty

    Assert.Contains("Solution", result)
