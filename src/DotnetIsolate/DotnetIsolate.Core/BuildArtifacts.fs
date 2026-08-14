module DotnetIsolate.Core.BuildArtifacts

open System
open System.IO

/// Directory names that hold generated build output *when a project file sits beside them*
/// (FR-15 rule A). The anchor is what makes the rule safe: `Web/bin` next to `Web.csproj` is
/// build output, while a checked-in `tools/bin/build.sh` is not. `TestResults` belongs here
/// rather than in the unanchored list for the same reason - a test run creates it next to the
/// test project, but the name is plausible enough for a fixture directory to warrant the anchor.
let projectAnchoredDirectoryNames = [ "bin"; "obj"; "TestResults" ]

/// Directory names that are package-manager restore output wherever they appear (FR-15 rule B).
/// Same reasoning DESIGN.md gives for excluding the `Analyzer` and `Reference` item types: the
/// contents are restored inside the container from a manifest, so shipping them is pure cost -
/// and `node_modules` is usually the largest single source of cache churn in a repo that has one.
let unanchoredDirectoryNames = [ "node_modules" ]

/// Whether a *file name* is a generated build log or coverage report (FR-15 rule C). Suffix
/// matching, not substring: `my.binlog.txt` is somebody's notes, not a binary log.
let isGeneratedReportName (name: string) =
    let endsWith (suffix: string) =
        name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)

    endsWith ".binlog"
    || endsWith ".coverage"
    || endsWith ".cobertura.xml"
    || (name.StartsWith("coverage.", StringComparison.OrdinalIgnoreCase) && endsWith ".xml")

/// Whether a directory directly contains a project file. Injected so the rules stay pure and
/// testable; BuildArtifactsIo supplies the real, memoized filesystem implementation.
type ContainsProjectFile = string -> bool

/// Every ancestor directory of `file`, nearest first, ending at the filesystem root.
let private ancestorDirectories (file: string) : string list =
    let rec go (dir: string option) =
        match dir with
        | None -> []
        | Some d -> d :: go (Path.GetDirectoryName(d) |> Option.ofObj)

    go (Path.GetDirectoryName(file) |> Option.ofObj)

let private equalsIgnoreCase (a: string) (b: string) =
    String.Equals(a, b, StringComparison.OrdinalIgnoreCase)

/// Whether `file` is a generated build artifact rather than a build input (FR-15).
///
/// `declaredOutputDirectories` is rule D: absolute directories MSBuild itself named as output
/// (`ArtifactsPath`, `BaseOutputPath`, `BaseIntermediateOutputPath`). It exists because the
/// `UseArtifactsOutput` layout relocates everything to a repo-root `artifacts/` tree whose
/// `bin`/`obj` directories have no project file beside them, so rule A cannot see them.
///
/// Rule D only ever fires on paths a *hand-written* glob resolved. With `UseArtifactsOutput` on,
/// the SDK puts the entire `$(ArtifactsPath)/**` into DefaultItemExcludes - not merely the
/// evaluating project's own subdirectory - so the default globs never produce these paths
/// (verified directly). That is not a reason to drop the rule: a `<None Include="**/*"/>` carries
/// no such exclusion, and that glob is the same mechanism that motivates rule A.
let isBuildArtifact
    (containsProjectFile: ContainsProjectFile)
    (declaredOutputDirectories: string list)
    (file: string)
    : bool =
    let underDeclaredOutput =
        declaredOutputDirectories |> List.exists (fun dir -> MirrorRoot.isUnder dir file)

    let isGeneratedReport = isGeneratedReportName (Path.GetFileName(file))

    let underArtifactDirectory () =
        ancestorDirectories file
        |> List.exists (fun dir ->
            let name = Path.GetFileName(dir)

            if unanchoredDirectoryNames |> List.exists (equalsIgnoreCase name) then
                true
            elif projectAnchoredDirectoryNames |> List.exists (equalsIgnoreCase name) then
                match Path.GetDirectoryName(dir) |> Option.ofObj with
                | Some parent -> containsProjectFile parent
                | None -> false
            else
                false)

    underDeclaredOutput || isGeneratedReport || underArtifactDirectory ()

/// A file list split by `isBuildArtifact`.
type Partition = { Kept: string list; Excluded: string list }

/// Splits `files` into genuine build inputs and generated artifacts, preserving input order.
let partition
    (containsProjectFile: ContainsProjectFile)
    (declaredOutputDirectories: string list)
    (files: string list)
    : Partition =
    let excluded, kept =
        files |> List.partition (isBuildArtifact containsProjectFile declaredOutputDirectories)

    { Kept = kept; Excluded = excluded }
