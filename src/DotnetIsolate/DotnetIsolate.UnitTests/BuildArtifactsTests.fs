module DotnetIsolate.UnitTests.BuildArtifactsTests

open Xunit
open DotnetIsolate.Core
open DotnetIsolate.UnitTests.PathHelpers

/// A ContainsProjectFile that says yes for exactly the directories given.
let private projectsIn (dirs: string list) : BuildArtifacts.ContainsProjectFile =
    let set = Set.ofList dirs
    fun dir -> Set.contains dir set

[<Fact>]
let ``rule A excludes obj content when a project file sits beside it`` () =
    let app = path [ "repo"; "App" ]
    let file = path [ "repo"; "App"; "obj"; "project.assets.json" ]

    Assert.True(BuildArtifacts.isBuildArtifact (projectsIn [ app ]) [] file)

[<Fact>]
let ``rule A keeps a bin directory with no project file beside it`` () =
    let file = path [ "repo"; "tools"; "bin"; "build.sh" ]

    Assert.False(BuildArtifacts.isBuildArtifact (projectsIn []) [] file)

[<Fact>]
let ``rule A excludes deeply nested build output`` () =
    let app = path [ "repo"; "App" ]
    let file = path [ "repo"; "App"; "obj"; "Debug"; "net8.0"; "App.AssemblyInfo.cs" ]

    Assert.True(BuildArtifacts.isBuildArtifact (projectsIn [ app ]) [] file)

[<Fact>]
let ``rule A excludes TestResults beside a project file`` () =
    let tests = path [ "repo"; "Tests" ]
    let file = path [ "repo"; "Tests"; "TestResults"; "run.trx" ]

    Assert.True(BuildArtifacts.isBuildArtifact (projectsIn [ tests ]) [] file)

[<Fact>]
let ``rule A is case-insensitive`` () =
    let app = path [ "repo"; "App" ]
    let file = path [ "repo"; "App"; "OBJ"; "project.assets.json" ]

    Assert.True(BuildArtifacts.isBuildArtifact (projectsIn [ app ]) [] file)

// Segment-wise, not prefix: "obj2" is not an obj directory.
[<Fact>]
let ``rule A does not fire on a prefix-sharing sibling directory`` () =
    let app = path [ "repo"; "App" ]
    let file = path [ "repo"; "App"; "obj2"; "notes.txt" ]

    Assert.False(BuildArtifacts.isBuildArtifact (projectsIn [ app ]) [] file)

[<Fact>]
let ``rule B excludes node_modules with no anchor`` () =
    let file = path [ "repo"; "Web"; "node_modules"; "pkg"; "index.js" ]

    Assert.True(BuildArtifacts.isBuildArtifact (projectsIn []) [] file)

[<Fact>]
let ``rule C excludes build logs and coverage reports`` () =
    let predicate = projectsIn []

    Assert.True(BuildArtifacts.isBuildArtifact predicate [] (path [ "repo"; "msbuild.binlog" ]))
    Assert.True(BuildArtifacts.isBuildArtifact predicate [] (path [ "repo"; "coverage.cobertura.xml" ]))
    Assert.True(BuildArtifacts.isBuildArtifact predicate [] (path [ "repo"; "run.coverage" ]))

[<Fact>]
let ``rule C does not fire on a file merely containing a pattern`` () =
    let predicate = projectsIn []

    Assert.False(BuildArtifacts.isBuildArtifact predicate [] (path [ "repo"; "my.binlog.txt" ]))
    Assert.False(BuildArtifacts.isBuildArtifact predicate [] (path [ "repo"; "notes.md" ]))

[<Fact>]
let ``rule D excludes files under a declared output directory`` () =
    let artifacts = path [ "repo"; "artifacts" ]
    let file = path [ "repo"; "artifacts"; "obj"; "App"; "project.assets.json" ]

    Assert.True(BuildArtifacts.isBuildArtifact (projectsIn []) [ artifacts ] file)

[<Fact>]
let ``rule D compares segment-wise, not by string prefix`` () =
    let artifacts = path [ "repo"; "artifacts" ]
    let file = path [ "repo"; "artifacts2"; "src"; "Program.cs" ]

    Assert.False(BuildArtifacts.isBuildArtifact (projectsIn []) [ artifacts ] file)

[<Fact>]
let ``an empty declared-directory list excludes nothing by rule D`` () =
    let file = path [ "repo"; "src"; "Program.cs" ]

    Assert.False(BuildArtifacts.isBuildArtifact (projectsIn []) [] file)

[<Fact>]
let ``partition splits kept from excluded and preserves order`` () =
    let app = path [ "repo"; "App" ]
    let source = path [ "repo"; "App"; "Program.cs" ]
    let artifact = path [ "repo"; "App"; "obj"; "project.assets.json" ]
    let other = path [ "repo"; "App"; "appsettings.json" ]

    let result = BuildArtifacts.partition (projectsIn [ app ]) [] [ source; artifact; other ]

    Assert.Equal<string list>([ source; other ], result.Kept)
    Assert.Equal<string list>([ artifact ], result.Excluded)
