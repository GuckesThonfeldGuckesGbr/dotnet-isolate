module DotnetIsolate.Core.FileResolution

/// The MSBuild item types that make up a project's build-relevant files (FR-4). ProjectReference
/// is resolved separately by ProjectGraph/MsBuild.projectReferenceResolver.
let fileItemTypes = [ "Compile"; "Content"; "None"; "EmbeddedResource" ]

/// Resolves a project's MSBuild items (item type -> absolute paths).
type ProjectItemsResolver = string -> Map<string, string list>

/// Resolves the deduplicated, flat set of build-relevant file paths for a single project (FR-4).
let resolveFiles (getItems: ProjectItemsResolver) (projectPath: string) : string list =
    getItems projectPath |> Map.toList |> List.collect snd |> List.distinct

/// Resolves the deduplicated, flat set of build-relevant file paths across every project in
/// `projects` (pipeline step 2 in DESIGN.md) - e.g. the full set returned by ProjectGraph.resolve.
let resolveAllFiles (getItems: ProjectItemsResolver) (projects: string list) : string list =
    projects |> List.collect (resolveFiles getItems) |> List.distinct

/// A `ProjectItemsResolver` backed by real MSBuild evaluation.
let projectItemsResolver: ProjectItemsResolver =
    fun projectPath -> MsBuild.getItems projectPath fileItemTypes
