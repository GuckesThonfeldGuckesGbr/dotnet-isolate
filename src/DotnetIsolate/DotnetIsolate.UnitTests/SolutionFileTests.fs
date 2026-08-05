module DotnetIsolate.UnitTests.SolutionFileTests

open Xunit
open DotnetIsolate.Core.SolutionFile

let private sampleSln =
    "\r\n\
Microsoft Visual Studio Solution File, Format Version 12.00\r\n\
# Visual Studio Version 17\r\n\
VisualStudioVersion = 17.0.31903.59\r\n\
MinimumVisualStudioVersion = 10.0.40219.1\r\n\
Project(\"{F2A71F9B-5D33-465A-A702-920D77279786}\") = \"A\", \"A\\A.fsproj\", \"{AAAAAAAA-0000-0000-0000-000000000001}\"\r\n\
EndProject\r\n\
Project(\"{F2A71F9B-5D33-465A-A702-920D77279786}\") = \"B\", \"B\\B.fsproj\", \"{BBBBBBBB-0000-0000-0000-000000000002}\"\r\n\
EndProject\r\n\
Global\r\n\
\tGlobalSection(SolutionConfigurationPlatforms) = preSolution\r\n\
\t\tDebug|Any CPU = Debug|Any CPU\r\n\
\tEndGlobalSection\r\n\
\tGlobalSection(ProjectConfigurationPlatforms) = postSolution\r\n\
\t\t{AAAAAAAA-0000-0000-0000-000000000001}.Debug|Any CPU.ActiveCfg = Debug|Any CPU\r\n\
\t\t{BBBBBBBB-0000-0000-0000-000000000002}.Debug|Any CPU.ActiveCfg = Debug|Any CPU\r\n\
\tEndGlobalSection\r\n\
EndGlobal\r\n"

[<Fact>]
let ``keeps only project entries whose resolved path is included`` () =
    let result = filterSln "/repo" (Set [ "/repo/A/A.fsproj" ]) sampleSln

    Assert.Contains("\"A\", \"A\\A.fsproj\"", result)
    Assert.DoesNotContain("\"B\", \"B\\B.fsproj\"", result)

[<Fact>]
let ``drops ProjectConfigurationPlatforms lines for excluded projects`` () =
    let result = filterSln "/repo" (Set [ "/repo/A/A.fsproj" ]) sampleSln

    Assert.Contains("{AAAAAAAA-0000-0000-0000-000000000001}.Debug|Any CPU.ActiveCfg", result)
    Assert.DoesNotContain("{BBBBBBBB-0000-0000-0000-000000000002}", result)

[<Fact>]
let ``keeps non-GUID-specific global sections verbatim`` () =
    let result = filterSln "/repo" (Set [ "/repo/A/A.fsproj" ]) sampleSln

    Assert.Contains("Debug|Any CPU = Debug|Any CPU", result)

[<Fact>]
let ``preserves the header lines before the first project`` () =
    let result = filterSln "/repo" (Set [ "/repo/A/A.fsproj" ]) sampleSln

    Assert.Contains("Microsoft Visual Studio Solution File, Format Version 12.00", result)
    Assert.Contains("MinimumVisualStudioVersion = 10.0.40219.1", result)

[<Fact>]
let ``a solution folder entry is dropped since its declared path never matches a real project`` () =
    let slnWithFolder =
        sampleSln.Replace(
            "EndProject\r\nGlobal\r\n",
            "EndProject\r\nProject(\"{2150E333-8FDC-42A3-9474-1A3956D46DE8}\") = \"Docs\", \"Docs\", \"{CCCCCCCC-0000-0000-0000-000000000003}\"\r\nEndProject\r\nGlobal\r\n"
        )

    let result = filterSln "/repo" (Set [ "/repo/A/A.fsproj" ]) slnWithFolder

    Assert.DoesNotContain("Docs", result)

[<Fact>]
let ``NestedProjects lines are dropped entirely, since solution folders are never kept`` () =
    let slnWithNesting =
        sampleSln.Replace(
            "\tEndGlobalSection\r\nEndGlobal\r\n",
            "\tEndGlobalSection\r\n\tGlobalSection(NestedProjects) = preSolution\r\n\t\t{AAAAAAAA-0000-0000-0000-000000000001} = {CCCCCCCC-0000-0000-0000-000000000003}\r\n\tEndGlobalSection\r\nEndGlobal\r\n"
        )

    let result = filterSln "/repo" (Set [ "/repo/A/A.fsproj" ]) slnWithNesting

    Assert.DoesNotContain("{AAAAAAAA-0000-0000-0000-000000000001} = {CCCCCCCC-0000-0000-0000-000000000003}", result)
    // The section wrapper itself is harmless to leave in (as an empty section) - only its
    // GUID-mapping content is dropped.
    Assert.Contains("GlobalSection(NestedProjects)", result)

[<Fact>]
let ``a solution file with no Global section at all is handled without crashing`` () =
    let noGlobalSection =
        "Microsoft Visual Studio Solution File, Format Version 12.00\r\n\
Project(\"{F2A71F9B-5D33-465A-A702-920D77279786}\") = \"A\", \"A\\A.fsproj\", \"{AAAAAAAA-0000-0000-0000-000000000001}\"\r\n\
EndProject\r\n"

    let result = filterSln "/repo" (Set [ "/repo/A/A.fsproj" ]) noGlobalSection

    Assert.Contains("\"A\", \"A\\A.fsproj\"", result)
