module DotnetIsolate.UnitTests.PhaseTests

open Xunit
open DotnetIsolate.Core
open DotnetIsolate.UnitTests.PathHelpers

let private projects = [ path [ "repo"; "A"; "A.fsproj" ]; path [ "repo"; "B"; "B.fsproj" ] ]

[<Fact>]
let ``restoreSubset keeps the project files`` () =
    let result = Phase.restoreSubset projects projects

    Assert.Equal<string list>(projects, result)

[<Fact>]
let ``restoreSubset keeps the build files restore evaluates`` () =
    let props = path [ "repo"; "Directory.Build.props" ]
    let targets = path [ "repo"; "Directory.Build.targets" ]
    let packages = path [ "repo"; "Directory.Packages.props" ]
    let nuget = path [ "repo"; "NuGet.config" ]
    let globalJson = path [ "repo"; "global.json" ]
    let lockFile = path [ "repo"; "A"; "packages.lock.json" ]

    let files = props :: targets :: packages :: nuget :: globalJson :: lockFile :: projects

    let result = Phase.restoreSubset projects files

    for expected in [ props; targets; packages; nuget; globalJson; lockFile ] do
        Assert.Contains(expected, result)

// Restore never reads .editorconfig, and including it would invalidate the cached restore layer
// on every formatting-rule edit.
[<Fact>]
let ``restoreSubset drops .editorconfig`` () =
    let editorConfig = path [ "repo"; ".editorconfig" ]

    let result = Phase.restoreSubset projects (editorConfig :: projects)

    Assert.DoesNotContain(editorConfig, result)

[<Fact>]
let ``restoreSubset drops sources and content`` () =
    let source = path [ "repo"; "A"; "Program.fs" ]
    let settings = path [ "repo"; "A"; "appsettings.json" ]
    let ruleset = path [ "repo"; "analysis.ruleset" ]

    let result = Phase.restoreSubset projects (source :: settings :: ruleset :: projects)

    Assert.DoesNotContain(source, result)
    Assert.DoesNotContain(settings, result)
    Assert.DoesNotContain(ruleset, result)

// Casing varies in the wild - "nuget.config" is as common as "NuGet.config", and on Linux both
// are distinct filenames.
[<Fact>]
let ``restoreSubset matches build file names case-insensitively`` () =
    let nuget = path [ "repo"; "nuget.config" ]

    let result = Phase.restoreSubset projects (nuget :: projects)

    Assert.Contains(nuget, result)
