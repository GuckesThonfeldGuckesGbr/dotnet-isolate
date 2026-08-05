module DotnetIsolate.Core.FileResolution

/// Resolves a project's MSBuild items (item type -> absolute paths).
type ProjectItemsResolver = string -> Map<string, string list>

/// Resolves the deduplicated, flat set of build-relevant file paths for a single project (FR-4) -
/// including the project file itself, which `dotnet msbuild -getItem` never returns (a .fsproj/
/// .csproj isn't a Compile/Content/None/EmbeddedResource item), but which is obviously needed to
/// build the isolated output at all.
let resolveFiles (getItems: ProjectItemsResolver) (projectPath: string) : string list =
    let items = getItems projectPath |> Map.toList |> List.collect snd
    projectPath :: items |> List.distinct

/// Resolves the deduplicated, flat set of build-relevant file paths across every project in
/// `projects` (pipeline step 2 in DESIGN.md) - e.g. the full set returned by ProjectGraph.resolve.
let resolveAllFiles (getItems: ProjectItemsResolver) (projects: string list) : string list =
    projects |> List.collect (resolveFiles getItems) |> List.distinct
