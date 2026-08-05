module DotnetIsolate.Core.FileResolution

/// The MSBuild item types that make up a project's build-relevant files (FR-4). A resolver's map
/// may carry other item types too (e.g. ProjectReference, when the caller shares one combined
/// MSBuild query across pipeline steps) - only these are ever treated as files.
let fileItemTypes = [ "Compile"; "Content"; "None"; "EmbeddedResource" ]

/// Resolves a project's MSBuild items (item type -> absolute paths).
type ProjectItemsResolver = string -> Map<string, string list>

/// Resolves the deduplicated, flat set of build-relevant file paths for a single project (FR-4) -
/// including the project file itself, which `dotnet msbuild -getItem` never returns (a .fsproj/
/// .csproj isn't a Compile/Content/None/EmbeddedResource item), but which is obviously needed to
/// build the isolated output at all.
let resolveFiles (getItems: ProjectItemsResolver) (projectPath: string) : string list =
    let items = getItems projectPath

    let files =
        fileItemTypes |> List.collect (fun t -> items |> Map.tryFind t |> Option.defaultValue [])

    projectPath :: files |> List.distinct

/// Resolves the deduplicated, flat set of build-relevant file paths across every project in
/// `projects` (pipeline step 2 in DESIGN.md) - e.g. the full set returned by ProjectGraph.resolve.
let resolveAllFiles (getItems: ProjectItemsResolver) (projects: string list) : string list =
    projects |> List.collect (resolveFiles getItems) |> List.distinct
