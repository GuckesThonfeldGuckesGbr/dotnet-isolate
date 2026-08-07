module DotnetIsolate.Core.FileResolution

open System.IO

/// The MSBuild item types whose `FullPath` is a build-relevant file, unconditionally (FR-4). A
/// resolver's map may carry other item types too (e.g. ProjectReference, when the caller shares
/// one combined MSBuild query across pipeline steps) - only these are ever treated as files.
///
/// Every entry here is *authored* in the project (or in a props file it imports), so its path is
/// taken at face value even when it points outside the solution tree - a `<Compile Include=
/// "../../shared/Version.cs"/>` link is a deliberate choice by whoever wrote it. Deliberately
/// absent: `Analyzer` and `Reference`, which resolve into the SDK install and the NuGet cache
/// respectively (verified directly) - those are restored inside the container, and copying them
/// would drag the mirror root out of the repo entirely.
let fileItemTypes =
    [ "Compile"
      "Content"
      "None"
      "EmbeddedResource"
      // Analyzer inputs: how StyleCop's stylecop.json and .NET analyzer config files reach csc.
      "AdditionalFiles"
      // WPF: XAML pages, the app definition, and non-embedded resources.
      "Page"
      "ApplicationDefinition"
      "Resource"
      // Microsoft.TypeScript.MSBuild projects.
      "TypeScriptCompile" ]

/// MSBuild item types MSBuild *auto-discovers* by globbing up the directory tree rather than from
/// anything the project author wrote. `EditorConfigFiles` is the SDK's ancestor walk for
/// `.editorconfig` (it resolves nested ones under the project too, which is why this is an item
/// query and not another entry in ImplicitFiles' well-known-name walk-up).
///
/// These are kept separate from `fileItemTypes` because auto-discovery has no natural stopping
/// point: a stray `~/.editorconfig` with no `root = true` resolves for every project in the repo,
/// and copying it in would pull the mirror root all the way up to the home directory. Callers
/// therefore bound these by the solution root, exactly as FR-5 already bounds its walk-up.
let ancestorGlobbedItemTypes = [ "EditorConfigFiles" ]

/// MSBuild *properties* whose value is a path to a build-relevant input file (FR-9). Some inputs
/// aren't items at all: `<CodeAnalysisRuleSet>` names a `.ruleset` the compiler reads, and without
/// it the isolated build fails to open the rule set. Only *input* path properties belong here -
/// e.g. `DocumentationFile` is deliberately absent, since it names a file the compiler *writes*.
let filePathPropertyNames =
    [ "CodeAnalysisRuleSet"
      "AssemblyOriginatorKeyFile"
      "ApplicationIcon"
      "ApplicationManifest"
      "Win32Resource"
      "Win32Manifest" ]

/// Resolves a project's MSBuild items (item type -> absolute paths).
type ProjectItemsResolver = string -> Map<string, string list>

/// Resolves a project's MSBuild properties (property name -> evaluated value).
type ProjectPropertiesResolver = string -> Map<string, string>

/// Everything file resolution needs from the outside world, bundled so the two entry points below
/// stay readable as the set of inputs grows.
type Resolvers =
    { GetItems: ProjectItemsResolver
      GetProperties: ProjectPropertiesResolver
      /// Whether a path exists on disk. Applied only to property-derived paths - see
      /// `resolvePropertyFiles`.
      FileExists: string -> bool
      /// The directory that bounds `ancestorGlobbedItemTypes`, i.e. the solution root (FR-8).
      /// `None` when no solution was found, in which case nothing is bounded.
      AncestorCeiling: string option }

let private itemsOfTypes (types: string list) (items: Map<string, string list>) : string list =
    types |> List.collect (fun t -> items |> Map.tryFind t |> Option.defaultValue [])

/// Resolves `filePathPropertyNames` to absolute file paths for one project. Unlike items - whose
/// `FullPath` metadata MSBuild resolves for us - a property's value is just the evaluated string,
/// typically written relative to the project file (`..\..\analysis.ruleset`), so it's made
/// absolute against the project's own directory here. Unset properties come back as empty strings.
///
/// Non-existent paths are dropped rather than passed on: a property is a single scalar that any
/// SDK or props file is free to default to something that was never meant to be read (a generated
/// path under `obj/`, say), and one such value would otherwise fail the whole run when
/// materialization tried to copy it.
///
/// `projectPath` must be *fully qualified* - not merely rooted. On Windows those differ, and
/// Path.GetFullPath's basePath overload rejects the latter ("\repo\A" has no drive). Pipeline.fs
/// passes everything through Path.GetFullPath before it gets here.
let resolvePropertyFiles (fileExists: string -> bool) (properties: Map<string, string>) (projectPath: string) =
    let projectDir = Path.GetDirectoryName(projectPath: string)

    filePathPropertyNames
    |> List.choose (fun name -> properties |> Map.tryFind name)
    |> List.map (fun value -> value.Trim())
    |> List.filter (fun value -> value <> "")
    |> List.map (fun value -> Path.GetFullPath(value, projectDir))
    |> List.filter fileExists

/// Resolves the deduplicated, flat set of build-relevant file paths for a single project (FR-4,
/// FR-9) - including the project file itself, which `dotnet msbuild -getItem` never returns (a
/// .fsproj/.csproj isn't a Compile/Content/None/... item of itself), but which is obviously needed
/// to build the isolated output at all.
let resolveFiles (resolvers: Resolvers) (projectPath: string) : string list =
    let items = resolvers.GetItems projectPath

    let withinCeiling =
        match resolvers.AncestorCeiling with
        | Some ceiling -> List.filter (MirrorRoot.isUnder ceiling)
        | None -> id

    let ancestorGlobbedFiles = itemsOfTypes ancestorGlobbedItemTypes items |> withinCeiling

    let propertyFiles =
        resolvePropertyFiles resolvers.FileExists (resolvers.GetProperties projectPath) projectPath

    projectPath :: (itemsOfTypes fileItemTypes items @ ancestorGlobbedFiles @ propertyFiles)
    |> List.distinct

/// Resolves the deduplicated, flat set of build-relevant file paths across every project in
/// `projects` (pipeline step 2 in DESIGN.md) - e.g. the full set returned by ProjectGraph.resolve.
let resolveAllFiles (resolvers: Resolvers) (projects: string list) : string list =
    projects |> List.collect (resolveFiles resolvers) |> List.distinct
