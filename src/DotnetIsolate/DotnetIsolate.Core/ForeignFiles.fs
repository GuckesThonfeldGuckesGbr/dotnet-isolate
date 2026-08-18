module DotnetIsolate.Core.ForeignFiles

open System.IO

/// Resolved files sharing one owning project directory that is not in the isolated closure.
type ForeignGroup = { Directory: string; Files: string list }

/// The nearest ancestor directory of `file` that directly contains a project file, if any.
///
/// "Nearest" is what makes the answer meaningful: a file under `App/Models/` is owned by `App`,
/// not by whatever solution-level project may sit further up. `None` is the important case - it
/// means no project owns the directory at all, as for a `../../shared/Version.cs` link, and such a
/// file is never treated as foreign (FR-16).
let owningProjectDirectory (containsProjectFile: BuildArtifacts.ContainsProjectFile) (file: string) : string option =
    let rec go (dir: string option) =
        match dir with
        | None -> None
        | Some d when containsProjectFile d -> Some d
        | Some d -> go (Path.GetDirectoryName(d) |> Option.ofObj)

    go (Path.GetDirectoryName(file) |> Option.ofObj)

/// Groups those of `files` whose owning project directory lies outside the closure (FR-16).
///
/// Detection only - nothing here removes a file from the output. A resolved path under a foreign
/// project is usually an over-reaching glob, but it can equally be a deliberate cross-project link,
/// and the two are indistinguishable once MSBuild has evaluated them to bare paths. Dropping would
/// therefore have to guess, and guessing wrong deletes a compilation input and breaks the isolated
/// build; reporting hands the ambiguity to the one party that can settle it. See
/// docs/superpowers/specs/2026-08-18-foreign-project-files-design.md.
///
/// Groups appear in the order their directory is first seen, and files keep their input order, so
/// the report is deterministic across runs (REL-1).
let detect
    (containsProjectFile: BuildArtifacts.ContainsProjectFile)
    (closureProjectDirectories: string list)
    (files: string list)
    : ForeignGroup list =
    let inClosure (dir: string) =
        closureProjectDirectories |> List.exists (MirrorRoot.sameDirectory dir)

    files
    |> List.choose (fun file ->
        match owningProjectDirectory containsProjectFile file with
        | Some dir when not (inClosure dir) -> Some(dir, file)
        | _ -> None)
    |> List.groupBy fst
    |> List.map (fun (dir, entries) ->
        { Directory = dir
          Files = entries |> List.map snd })
