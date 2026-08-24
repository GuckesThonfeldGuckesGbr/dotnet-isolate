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

/// A ListFilesResult with nothing to report; tests set only the field they are about.
let private cleanListFiles: Pipeline.ListFilesResult =
    { Files = []
      Projects = []
      SolutionRoot = None
      ExcludedArtifacts = []
      ForeignProjectFiles = []
      MissingFiles = []
      EntriesOutsideDiscoveredSolution = [] }

[<Fact>]
let ``no warnings are produced for a clean list-files run`` () =
    Assert.Empty(Report.listFilesWarnings cleanListFiles)

[<Fact>]
let ``list-files warnings summarise excluded artifacts the same way isolate does`` () =
    let result =
        { cleanListFiles with
            ExcludedArtifacts = [ path [ "a" ]; path [ "b" ]; path [ "c" ] ] }

    let line = Report.listFilesWarnings result |> List.exactlyOne

    Assert.Contains("3", line)
    Assert.Contains("build artifact", line)

[<Fact>]
let ``list-files warnings report foreign project files the same way isolate does`` () =
    let other = path [ "repo"; "Other" ]
    let first = path [ "repo"; "Other"; "a.cs" ]

    let group: ForeignFiles.ForeignGroup = { Directory = other; Files = [ first ] }

    let result = { cleanListFiles with ForeignProjectFiles = [ group ] }
    let line = Report.listFilesWarnings result |> List.exactlyOne

    Assert.Contains("1 file(s)", line)
    Assert.Contains(other, line)

[<Fact>]
let ``list-files warnings report missing files before entries outside the solution, not transposed`` () =
    let missing = path [ "repo"; "gone.txt" ]
    let outside = path [ "repo"; "Outside"; "B.fsproj" ]

    let solutionRoot: SolutionDiscovery.SolutionRoot =
        { Directory = path [ "repo" ]
          SolutionFile = path [ "repo"; "Fixture.sln" ]
          Source = SolutionDiscovery.AutoDiscovered }

    let result =
        { cleanListFiles with
            MissingFiles = [ missing ]
            EntriesOutsideDiscoveredSolution = [ outside ]
            SolutionRoot = Some solutionRoot }

    let warnings = Report.listFilesWarnings result

    Assert.Equal(2, warnings.Length)
    Assert.Contains(warnings, fun (w: string) -> w.Contains(missing) && w.Contains("does not exist"))
    Assert.Contains(warnings, fun (w: string) -> w.Contains(outside) && w.Contains("lies outside"))
