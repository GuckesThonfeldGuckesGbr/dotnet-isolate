module DotnetIsolate.UnitTests.SolutionFileXmlTests

open Xunit
open DotnetIsolate.Core.SolutionFileXml

let private sampleSlnx =
    "<Solution>\n\
  <Project Path=\"A/A.fsproj\" />\n\
  <Project Path=\"B/B.fsproj\" />\n\
</Solution>"

[<Fact>]
let ``keeps only project entries whose resolved path is included`` () =
    let result = filterSlnx "/repo" (Set [ "/repo/A/A.fsproj" ]) sampleSlnx

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

    let result = filterSlnx "/repo" (Set [ "/repo/A/A.fsproj" ]) slnxWithFolder

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

    let result = filterSlnx "/repo" (Set [ "/repo/A/A.fsproj" ]) slnxWithFolder

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

    let result = filterSlnx "/repo" (Set [ "/repo/A/A.fsproj" ]) slnxWithBackslash

    Assert.Contains("A\\A.fsproj", result)
    Assert.DoesNotContain("B\\B.fsproj", result)

[<Fact>]
let ``a solution with no projects at all is handled without crashing`` () =
    let empty = "<Solution>\n</Solution>"

    let result = filterSlnx "/repo" (Set []) empty

    Assert.Contains("Solution", result)
