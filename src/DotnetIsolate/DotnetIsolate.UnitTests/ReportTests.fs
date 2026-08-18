module DotnetIsolate.UnitTests.ReportTests

open Xunit
open DotnetIsolate.Core
open DotnetIsolate.UnitTests.PathHelpers

/// A result with nothing to report; tests set only the field they are about.
let private clean: Pipeline.IsolateResult =
    { OutputDir = path [ "repo"; "out" ]
      IncludedProjects = []
      FileCount = 0
      SolutionRoot = None
      Strategy = LinkStrategy.Copy
      ExcludedUnderOutput = []
      ExcludedArtifacts = []
      ForeignProjectFiles = []
      StaleEntries = []
      MissingFiles = []
      EntriesOutsideDiscoveredSolution = [] }

[<Fact>]
let ``no warnings are produced for a clean run`` () = Assert.Empty(Report.warnings clean)

[<Fact>]
let ``excluded artifacts collapse into a single counted line`` () =
    let result =
        { clean with
            ExcludedArtifacts = [ path [ "a" ]; path [ "b" ]; path [ "c" ] ] }

    let line = Report.warnings result |> List.exactlyOne

    Assert.Contains("3", line)
    Assert.Contains("build artifact", line)

[<Fact>]
let ``stale entries collapse into a single counted line mentioning --clean`` () =
    let result =
        { clean with
            StaleEntries = [ path [ "a" ]; path [ "b" ] ] }

    let line = Report.warnings result |> List.exactlyOne

    Assert.Contains("not clean", line)
    Assert.Contains("2", line)
    Assert.Contains("--clean", line)

// Per foreign directory, not per file: one over-reaching glob sweeps many files at once, and the
// directory is the thing the user has to go and fix.
[<Fact>]
let ``foreign project files collapse into one counted line per directory`` () =
    let other = path [ "repo"; "Other" ]
    let first = path [ "repo"; "Other"; "a.cs" ]
    let second = path [ "repo"; "Other"; "b.cs" ]

    let group: ForeignFiles.ForeignGroup =
        { Directory = other; Files = [ first; second ] }

    let result =
        { clean with ForeignProjectFiles = [ group ] }

    let line = Report.warnings result |> List.exactlyOne

    Assert.Contains("2 file(s)", line)
    Assert.Contains(other, line)
    Assert.Contains("not in the isolated closure", line)

// The two bulk categories are summarised; these two stay per file, because each path is
// separately actionable.
[<Fact>]
let ``missing files and files under the output are still reported per file`` () =
    let missing = path [ "repo"; "gone.txt" ]
    let underOutput = path [ "repo"; "out"; "stale.txt" ]

    let result =
        { clean with
            MissingFiles = [ missing ]
            ExcludedUnderOutput = [ underOutput ] }

    let warnings = Report.warnings result

    Assert.Equal(2, warnings.Length)
    Assert.Contains(warnings, fun (w: string) -> w.Contains(missing))
    Assert.Contains(warnings, fun (w: string) -> w.Contains(underOutput))
