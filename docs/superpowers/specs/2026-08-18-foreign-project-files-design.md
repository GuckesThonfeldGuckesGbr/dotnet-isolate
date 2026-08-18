# Reporting resolved inputs owned by projects outside the closure (FR-16)

## Problem

FR-1 says the isolated output holds the entry project's closure and *nothing more*. It does not
hold today. A hand-written glob — `<Compile Include="..\**\*.cs"/>`, or a root-level project with a
broad `<None Include="**\*.json"/>` — resolves files that live under an entirely different
project's directory. Those files are copied into the output, inflate the mirror root, and bust the
`COPY --from` cache key whenever they change, which is exactly the cost the tool exists to remove.

The FR-15 build-artifact work identified this and deferred it, proposing the aggressive fix: drop
any resolved file under a directory whose project is not in the closure.

## Why the aggressive fix is wrong

Two findings, both established before this design:

**FR-15's rules only ever drop files the build regenerates.** `bin/`, `obj/`, `TestResults`,
`node_modules`, declared output directories, build and coverage logs — every one is reproduced
inside the container by `restore`/`build`. That is what makes dropping them safe, and it is why the
E2E suite still builds green with `project.assets.json` dropped from a `**\*.json` glob.

**A foreign-directory file is the opposite: an input the build consumes.** Drop a
`<Compile Include="..\OtherProject\Shared.cs"/>` and the isolated build fails to compile. The
failure surfaces inside Docker, far from its cause, and it lands on a project that authored a
deliberate, legal cross-project link — a construct `FileResolution.fs` documents as intentional and
honours on purpose.

**The discriminator does not exist in the data.** After MSBuild evaluation the tool sees resolved
absolute paths. Whether a path came from a hand-authored `Include` naming one file or from a glob
that swept a whole subtree is not recoverable from `-getItem` output. So "deliberate link" and
"accidental sweep" are indistinguishable at the point where the rule would fire, and any rule that
drops must guess. Guessing wrong breaks a build.

Reporting cannot break a build. It converts a silent FR-1 violation into a fact the user can act
on, and the user *does* have the discriminator the tool lacks: they know whether they wrote that
glob on purpose. This matches how the project already treats an ambiguity it cannot settle — FR-14
reports missing item files rather than failing the run.

## Design

### The rule (new requirement FR-16)

A file's **owning project directory** is its nearest ancestor directory that directly contains a
`*.csproj`, `*.fsproj` or `*.vbproj`.

A resolved file is **foreign** when it has an owning project directory and that directory is not
one of the closure's own project directories.

A file with no owning project directory anywhere above it is never foreign. This is the deliberate
case the rule must stay quiet about: `..\..\shared\Version.cs` lives in a directory no project
owns, so a repo doing nothing wrong never sees the note. Restricting the rule this way is what
keeps it from firing on ordinary shared-file links and training users to ignore it.

Nothing is dropped. The materialized output is byte-identical with and without this change.

### Module

`ForeignFiles.fs`, pure, alongside `BuildArtifacts.fs`:

```fsharp
/// Files sharing one owning project directory outside the closure.
type ForeignGroup = { Directory: string; Files: string list }

val owningProjectDirectory : BuildArtifacts.ContainsProjectFile -> string -> string option
val detect : BuildArtifacts.ContainsProjectFile -> closureProjectDirectories: string list
           -> files: string list -> ForeignGroup list
```

It reuses `BuildArtifacts.ContainsProjectFile` and, at the pipeline, the already-memoized
`BuildArtifactsIo.containsProjectFile` — the same probe FR-15 rule A anchors on, so there is no new
IO file, no second cache, and no added directory enumeration in the common case.

Directory comparison goes through `MirrorRoot`'s segment-wise `OrdinalIgnoreCase` logic rather than
string equality. Case and trailing separators must not decide the answer on Windows or macOS.

`detect` groups rather than returning a flat list, so formatting stays a fold in `Report.fs` and the
grouping itself is unit-testable. Group order follows first appearance in `files`; file order within
a group is preserved, so a run is deterministic (REL-1).

### Pipeline placement

After the FR-15 artifact partition, over `artifactPartition.Kept`. A `bin/` file under a foreign
project is already excluded and must not be reported a second time. `IsolateResult` gains
`ForeignProjectFiles: ForeignGroup list`.

The closure's project directories are the directories of the projects `ProjectGraph` resolved, so
every project file is trivially non-foreign with respect to its own directory.

### Report

One line per group, `note:` rather than `warning:` — nothing was dropped, and the glob may be
deliberate:

    note: 3 file(s) resolved from /repo/OtherProject, whose project is not in the isolated closure

Per group and not per file: a glob that reaches into another project usually pulls in many files at
once, carrying one bit of information between them — the same reasoning `Report.fs` already applies
to artifact exclusions and stale entries.

## Testing

**Unit (`ForeignFiles`), against an injected probe:** owner inside the closure; owner outside it;
no owning directory at all; a file nested several levels below its owning project; a file directly
in a closure project's own directory; two foreign files under one directory grouping into one entry;
case-differing directory spellings comparing equal.

**Unit (`Report`):** the line renders with the right count, and is absent when no group exists.

**Integration:** a fixture project carrying a glob that reaches into a sibling project's directory,
asserting both halves — the note is reported, *and* the swept file is still present in the output.
The second assertion is what pins "detect, don't drop"; without it the test would pass under the
rejected design.

## Out of scope

- **Dropping foreign files**, under a flag or otherwise. Rejected above. If a real repo later shows
  unrelated sources reaching the output and the report proves insufficient, revisit it then — with
  that repo as evidence.
- **Reporting files merely outside the closure's directories.** Considered and rejected: it fires on
  the ordinary shared-file link, which is legal and common.
