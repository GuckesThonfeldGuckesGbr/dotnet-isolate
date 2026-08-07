module DotnetIsolate.UnitTests.FileResolutionTests

open Xunit
open DotnetIsolate.Core.FileResolution

/// Resolvers that see nothing: no items, no properties, every candidate path existing, no ceiling.
/// Tests override only the field they're about, so each one reads as exactly its own scenario.
let private stub: Resolvers =
    { GetItems = fun _ -> Map.empty
      GetProperties = fun _ -> Map.empty
      FileExists = fun _ -> true
      AncestorCeiling = None }

let private withItems (projects: Map<string, Map<string, string list>>) =
    { stub with GetItems = fun project -> projects |> Map.tryFind project |> Option.defaultValue Map.empty }

let private withProperties (projects: Map<string, Map<string, string>>) =
    { stub with
        GetProperties = fun project -> projects |> Map.tryFind project |> Option.defaultValue Map.empty }

[<Fact>]
let ``resolveFiles includes the project file itself, since -getItem never returns it`` () =
    let result = resolveFiles stub "A/A.fsproj"

    Assert.Equal<string list>([ "A/A.fsproj" ], result)

[<Fact>]
let ``resolveFiles flattens every item type plus the project file into one deduplicated list`` () =
    let resolvers =
        withItems (
            Map
                [ "A/A.fsproj",
                  Map [ "Compile", [ "A/Program.fs"; "A/Lib.fs" ]; "Content", [ "A/appsettings.json" ] ] ]
        )

    let result = resolveFiles resolvers "A/A.fsproj"

    Assert.Equal<Set<string>>(
        Set [ "A/A.fsproj"; "A/Program.fs"; "A/Lib.fs"; "A/appsettings.json" ],
        Set result
    )

[<Fact>]
let ``resolveFiles dedups a file that appears under more than one item type`` () =
    let resolvers =
        withItems (Map [ "A/A.fsproj", Map [ "Compile", [ "A/Program.fs" ]; "None", [ "A/Program.fs" ] ] ])

    let result = resolveFiles resolvers "A/A.fsproj"

    Assert.Equal<string list>([ "A/A.fsproj"; "A/Program.fs" ], result)

[<Fact>]
let ``resolveAllFiles aggregates and dedups files, including each project file, across multiple projects`` () =
    let resolvers =
        withItems (
            Map
                [ "A/A.fsproj", Map [ "Compile", [ "A/Program.fs" ] ]
                  "B/B.fsproj", Map [ "Compile", [ "B/Program.fs" ]; "Content", [ "Shared/settings.json" ] ]
                  "C/C.fsproj", Map [ "Content", [ "Shared/settings.json" ] ] ]
        )

    let result = resolveAllFiles resolvers [ "A/A.fsproj"; "B/B.fsproj"; "C/C.fsproj" ]

    Assert.Equal<Set<string>>(
        Set
            [ "A/A.fsproj"
              "A/Program.fs"
              "B/B.fsproj"
              "B/Program.fs"
              "C/C.fsproj"
              "Shared/settings.json" ],
        Set result
    )

/// Each entry in `fileItemTypes` earns its place by being a real compiler/build input, so none of
/// them may be silently dropped - and none of them is bounded by the ancestor ceiling, since an
/// authored `<Compile Include="../../shared/X.cs"/>` outside the solution tree is deliberate.
[<Fact>]
let ``resolveFiles includes every declared file item type, ceiling notwithstanding`` () =
    let itemsForEveryType = fileItemTypes |> List.map (fun t -> t, [ $"Outside/{t}.file" ]) |> Map.ofList

    let resolvers =
        { withItems (Map [ "A/A.fsproj", itemsForEveryType ]) with
            AncestorCeiling = Some(PathHelpers.path [ "repo" ]) }

    let result = resolveFiles resolvers "A/A.fsproj"

    Assert.Equal<Set<string>>(
        Set("A/A.fsproj" :: (fileItemTypes |> List.map (fun t -> $"Outside/{t}.file"))),
        Set result
    )

[<Fact>]
let ``resolveFiles includes AdditionalFiles, which is how analyzer config like stylecop.json arrives`` () =
    let resolvers = withItems (Map [ "A/A.fsproj", Map [ "AdditionalFiles", [ "A/stylecop.json" ] ] ])

    let result = resolveFiles resolvers "A/A.fsproj"

    Assert.Equal<Set<string>>(Set [ "A/A.fsproj"; "A/stylecop.json" ], Set result)

[<Fact>]
let ``resolveFiles keeps ancestor-globbed files that sit at or below the ceiling`` () =
    let projectPath = PathHelpers.path [ "repo"; "src"; "A"; "A.fsproj" ]
    let atCeiling = PathHelpers.path [ "repo"; ".editorconfig" ]
    let nested = PathHelpers.path [ "repo"; "src"; "A"; "Generated"; ".editorconfig" ]

    let resolvers =
        { withItems (Map [ projectPath, Map [ "EditorConfigFiles", [ atCeiling; nested ] ] ]) with
            AncestorCeiling = Some(PathHelpers.path [ "repo" ]) }

    let result = resolveFiles resolvers projectPath

    Assert.Equal<Set<string>>(Set [ projectPath; atCeiling; nested ], Set result)

/// The reason ancestor-globbed items are bounded at all: MSBuild's `.editorconfig` walk doesn't
/// stop at the repo, so a stray one in the user's home directory would otherwise be copied in -
/// dragging the mirror root (FR-2) up with it.
[<Fact>]
let ``resolveFiles drops ancestor-globbed files from above the ceiling`` () =
    let projectPath = PathHelpers.path [ "home"; "me"; "repo"; "src"; "A"; "A.fsproj" ]
    let inRepo = PathHelpers.path [ "home"; "me"; "repo"; ".editorconfig" ]
    let inHome = PathHelpers.path [ "home"; "me"; ".editorconfig" ]

    let resolvers =
        { withItems (Map [ projectPath, Map [ "EditorConfigFiles", [ inHome; inRepo ] ] ]) with
            AncestorCeiling = Some(PathHelpers.path [ "home"; "me"; "repo" ]) }

    let result = resolveFiles resolvers projectPath

    Assert.Equal<Set<string>>(Set [ projectPath; inRepo ], Set result)

/// A sibling directory whose name merely starts with the ceiling's is not below the ceiling.
[<Fact>]
let ``resolveFiles compares the ceiling by path segment, not by string prefix`` () =
    let projectPath = PathHelpers.path [ "repo"; "src"; "A"; "A.fsproj" ]
    let sibling = PathHelpers.path [ "repo2"; ".editorconfig" ]

    let resolvers =
        { withItems (Map [ projectPath, Map [ "EditorConfigFiles", [ sibling ] ] ]) with
            AncestorCeiling = Some(PathHelpers.path [ "repo" ]) }

    let result = resolveFiles resolvers projectPath

    Assert.Equal<string list>([ projectPath ], result)

/// No solution found (FR-8) means no ceiling, so nothing bounds the walk.
[<Fact>]
let ``resolveFiles keeps every ancestor-globbed file when there is no ceiling`` () =
    let projectPath = PathHelpers.path [ "repo"; "src"; "A"; "A.fsproj" ]
    let far = PathHelpers.path [ "home"; "me"; ".editorconfig" ]

    let resolvers = withItems (Map [ projectPath, Map [ "EditorConfigFiles", [ far ] ] ])

    let result = resolveFiles resolvers projectPath

    Assert.Equal<Set<string>>(Set [ projectPath; far ], Set result)

[<Fact>]
let ``resolveFiles resolves a relative CodeAnalysisRuleSet against the project directory`` () =
    let projectPath = PathHelpers.path [ "repo"; "src"; "A"; "A.fsproj" ]

    let resolvers =
        withProperties (Map [ projectPath, Map [ "CodeAnalysisRuleSet", "../../analysis.ruleset" ] ])

    let result = resolveFiles resolvers projectPath

    Assert.Equal<Set<string>>(
        Set [ projectPath; PathHelpers.path [ "repo"; "analysis.ruleset" ] ],
        Set result
    )

[<Fact>]
let ``resolveFiles keeps an already-absolute CodeAnalysisRuleSet as-is`` () =
    let projectPath = PathHelpers.path [ "repo"; "src"; "A"; "A.fsproj" ]
    let ruleSet = PathHelpers.path [ "repo"; "build"; "analysis.ruleset" ]
    let resolvers = withProperties (Map [ projectPath, Map [ "CodeAnalysisRuleSet", ruleSet ] ])

    let result = resolveFiles resolvers projectPath

    Assert.Equal<Set<string>>(Set [ projectPath; ruleSet ], Set result)

[<Fact>]
let ``resolveFiles ignores an unset CodeAnalysisRuleSet, which MSBuild reports as an empty string`` () =
    let projectPath = PathHelpers.path [ "repo"; "src"; "A"; "A.fsproj" ]
    let resolvers = withProperties (Map [ projectPath, Map [ "CodeAnalysisRuleSet", "  " ] ])

    let result = resolveFiles resolvers projectPath

    Assert.Equal<string list>([ projectPath ], result)

/// Every file-path property is resolved, not just the first one that happens to be set.
[<Fact>]
let ``resolveFiles resolves each declared file-path property`` () =
    let projectPath = PathHelpers.path [ "repo"; "A"; "A.fsproj" ]

    let properties = filePathPropertyNames |> List.map (fun name -> name, $"assets/{name}.file") |> Map.ofList

    let resolvers = withProperties (Map [ projectPath, properties ])

    let result = resolveFiles resolvers projectPath

    let expected =
        filePathPropertyNames
        |> List.map (fun name -> PathHelpers.path [ "repo"; "A"; "assets"; $"{name}.file" ])

    Assert.Equal<Set<string>>(Set(projectPath :: expected), Set result)

/// An SDK is free to default a path property to something that was never meant to be read, so a
/// property value pointing at nothing is skipped rather than failing the whole run downstream.
[<Fact>]
let ``resolveFiles drops a file-path property whose target does not exist`` () =
    let projectPath = PathHelpers.path [ "repo"; "A"; "A.fsproj" ]
    let real = PathHelpers.path [ "repo"; "A"; "analysis.ruleset" ]

    let resolvers =
        { withProperties (
            Map
                [ projectPath,
                  Map [ "CodeAnalysisRuleSet", "analysis.ruleset"; "ApplicationIcon", "generated/app.ico" ] ]
          ) with
            FileExists = fun path -> path = real }

    let result = resolveFiles resolvers projectPath

    Assert.Equal<Set<string>>(Set [ projectPath; real ], Set result)

[<Fact>]
let ``resolveAllFiles dedups one shared ruleset referenced by several projects`` () =
    let projectA = PathHelpers.path [ "repo"; "src"; "A"; "A.fsproj" ]
    let projectB = PathHelpers.path [ "repo"; "src"; "B"; "B.fsproj" ]
    let ruleSet = PathHelpers.path [ "repo"; "analysis.ruleset" ]

    let resolvers =
        withProperties (
            Map
                [ projectA, Map [ "CodeAnalysisRuleSet", "../../analysis.ruleset" ]
                  projectB, Map [ "CodeAnalysisRuleSet", "../../analysis.ruleset" ] ]
        )

    let result = resolveAllFiles resolvers [ projectA; projectB ]

    Assert.Equal<Set<string>>(Set [ projectA; projectB; ruleSet ], Set result)
    Assert.Equal(1, result |> List.filter (fun f -> f = ruleSet) |> List.length)
