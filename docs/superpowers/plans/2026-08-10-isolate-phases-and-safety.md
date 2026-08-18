# Output-Directory Safety, Restore Phase, Missing Files, and Multiple Entry Projects — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make the output directory safe to point anywhere, add a `--restore` phase that keeps Docker's `restore` layer cache-hit across source edits, warn instead of failing on referenced-but-absent files, and allow several entry projects in one run.

**Architecture:** Three new pure modules in `DotnetIsolate.Core` — `OutputSafety` (partition/validate inputs against the output directory), `Phase` (select the restore subset) — plus signature changes to `Materialize`, `FileResolution` and `ProjectGraph`. `Pipeline.isolate` wires them together and returns diagnostics; `Program.fs` owns all console output. The repo's convention is strict: pure logic in `X.fs`, filesystem/process IO in `XIo.fs`, because coverlet's exclude filter works only at whole-class granularity.

**Tech Stack:** F# on .NET 8, xUnit, Argu for CLI parsing, `dotnet msbuild -getItem/-getProperty` for evaluation, Docker buildx for E2E.

**Spec:** `docs/superpowers/specs/2026-08-10-isolate-phases-and-safety-design.md`

## Global Constraints

- **Everything must work on Linux, Windows and macOS (POR-1).** CI runs all three.
- **Never hardcode path literals like `"/repo/src/A"` in tests.** Use `DotnetIsolate.UnitTests.PathHelpers.path [ "repo"; "src"; "A" ]`, which builds a fully qualified, platform-native path. A bare `/repo` is not Windows' native root form and `Path.GetDirectoryName` will not walk it up.
- **Never assert on path *strings*.** Compare against a path built the same way, or assert on `Path.GetFileName`.
- **Rooted is not fully qualified.** `Path.GetFullPath(path, basePath)` throws for a rooted-but-driveless Windows path.
- **Path comparison is case-insensitive and segment-wise.** Use `MirrorRoot.isUnder` / `StringComparison.OrdinalIgnoreCase`; never raw string `StartsWith`.
- **Line endings are LF everywhere**, enforced by `.gitattributes`. The `.sln` the tool *generates* is deliberately CRLF — do not change that.
- **New `.fs` files must be added to the owning `.fsproj`'s `<Compile Include>` list in dependency order.** F# compilation is order-sensitive.
- Build and test commands, run from `src/DotnetIsolate`:
  - `dotnet build DotnetIsolate.sln`
  - `dotnet test DotnetIsolate.UnitTests/DotnetIsolate.UnitTests.fsproj`
  - `dotnet test DotnetIsolate.IntegrationTests/DotnetIsolate.IntegrationTests.fsproj`
  - `dotnet test DotnetIsolate.E2ETests/DotnetIsolate.E2ETests.fsproj` (needs a Docker daemon)

## File Structure

**Created:**
- `src/DotnetIsolate/DotnetIsolate.Core/OutputSafety.fs` — pure. Partitions the resolved input set against the output directory and validates the result.
- `src/DotnetIsolate/DotnetIsolate.Core/Phase.fs` — pure. Selects the `dotnet restore`-relevant subset of a resolved file set.
- `src/DotnetIsolate/DotnetIsolate.UnitTests/OutputSafetyTests.fs`
- `src/DotnetIsolate/DotnetIsolate.UnitTests/PhaseTests.fs`
- `src/DotnetIsolate/DotnetIsolate.E2ETests/Fixtures/TwoPhase/Dockerfile` — the buildx two-phase pattern the README documents.

**Modified:**
- `Materialize.fs` — drop the unconditional delete, add a `clean` parameter, report stale entries.
- `FileResolution.fs` — existence-filter item-derived paths and report what was dropped.
- `ProjectGraph.fs` — add `resolveMany`.
- `Pipeline.fs` — multiple entry projects, wiring for all of the above, richer result.
- `DotnetIsolate/Program.fs` — positional list, `--restore`, `--clean`, warning output.
- `DotnetIsolate.Core.fsproj`, `DotnetIsolate.UnitTests.fsproj` — compile lists.
- Unit tests: `FileResolutionTests.fs`, `ProjectGraphTests.fs`.
- Integration tests: `MaterializeTests.fs`, `PipelineTests.fs`, `FileResolutionTests.fs`.
- `DotnetIsolate.E2ETests/DockerHarness.fs` — expose `stepCached`, drop the classic-builder branch.
- `DotnetIsolate.E2ETests/DockerCacheTests.fs` — buildx only, add the inside-the-closure cache test.
- `README.md`, `REQUIREMENTS.md`, `DESIGN.md`.

---

### Task 1: `OutputSafety` — partition and validate inputs against the output directory

The key insight: excluding files under the output directory and erroring when the output would consume its own inputs are the *same* predicate with different outcomes. Resolve it by excluding first, then failing only if nothing survives. A nested `out/` loses just its stale contents; an output directory sitting at the solution root loses everything, which is the error case.

**Files:**
- Create: `src/DotnetIsolate/DotnetIsolate.Core/OutputSafety.fs`
- Create: `src/DotnetIsolate/DotnetIsolate.UnitTests/OutputSafetyTests.fs`
- Modify: `src/DotnetIsolate/DotnetIsolate.Core/DotnetIsolate.Core.fsproj`
- Modify: `src/DotnetIsolate/DotnetIsolate.UnitTests/DotnetIsolate.UnitTests.fsproj`

**Interfaces:**
- Consumes: `MirrorRoot.isUnder : directory:string -> path:string -> bool` (already exists).
- Produces:
  - `type OutputSafety.InputPartition = { Kept: string list; ExcludedUnderOutput: string list }`
  - `OutputSafety.partitionInputs : outputDir:string -> files:string list -> InputPartition`
  - `OutputSafety.validate : outputDir:string -> partition:InputPartition -> Result<unit, string>`

- [x] **Step 1: Write the failing tests**

Create `src/DotnetIsolate/DotnetIsolate.UnitTests/OutputSafetyTests.fs`:

```fsharp
module DotnetIsolate.UnitTests.OutputSafetyTests

open Xunit
open DotnetIsolate.Core
open DotnetIsolate.UnitTests.PathHelpers

[<Fact>]
let ``partitionInputs keeps files outside the output directory`` () =
    let outputDir = path [ "repo"; "out" ]
    let a = path [ "repo"; "src"; "A"; "A.fsproj" ]
    let b = path [ "repo"; "src"; "B"; "B.fsproj" ]

    let result = OutputSafety.partitionInputs outputDir [ a; b ]

    Assert.Equal<string list>([ a; b ], result.Kept)
    Assert.Empty(result.ExcludedUnderOutput)

[<Fact>]
let ``partitionInputs excludes files under the output directory`` () =
    let outputDir = path [ "repo"; "src"; "A"; "out" ]
    let source = path [ "repo"; "src"; "A"; "A.fsproj" ]
    let stale = path [ "repo"; "src"; "A"; "out"; "src"; "B"; "B.fsproj" ]

    let result = OutputSafety.partitionInputs outputDir [ source; stale ]

    Assert.Equal<string list>([ source ], result.Kept)
    Assert.Equal<string list>([ stale ], result.ExcludedUnderOutput)

// The output directory itself is "under" itself, so a file sitting directly in it is excluded.
[<Fact>]
let ``partitionInputs excludes a file directly inside the output directory`` () =
    let outputDir = path [ "repo"; "out" ]
    let inside = path [ "repo"; "out"; "A.fsproj" ]

    let result = OutputSafety.partitionInputs outputDir [ inside ]

    Assert.Empty(result.Kept)
    Assert.Equal<string list>([ inside ], result.ExcludedUnderOutput)

// Segment-wise comparison: "out2" must not be mistaken for a child of "out".
[<Fact>]
let ``partitionInputs does not treat a prefix-sharing sibling as inside the output directory`` () =
    let outputDir = path [ "repo"; "out" ]
    let sibling = path [ "repo"; "out2"; "A.fsproj" ]

    let result = OutputSafety.partitionInputs outputDir [ sibling ]

    Assert.Equal<string list>([ sibling ], result.Kept)
    Assert.Empty(result.ExcludedUnderOutput)

[<Fact>]
let ``partitionInputs matches case-insensitively`` () =
    let outputDir = path [ "repo"; "Out" ]
    let inside = path [ "repo"; "out"; "A.fsproj" ]

    let result = OutputSafety.partitionInputs outputDir [ inside ]

    Assert.Equal<string list>([ inside ], result.ExcludedUnderOutput)

[<Fact>]
let ``validate succeeds when files survive the partition`` () =
    let outputDir = path [ "repo"; "out" ]
    let partition =
        { OutputSafety.Kept = [ path [ "repo"; "src"; "A"; "A.fsproj" ] ]
          OutputSafety.ExcludedUnderOutput = [] }

    Assert.Equal(Ok(), OutputSafety.validate outputDir partition)

[<Fact>]
let ``validate fails when the output directory consumed every input`` () =
    let outputDir = path [ "repo" ]
    let partition =
        { OutputSafety.Kept = []
          OutputSafety.ExcludedUnderOutput = [ path [ "repo"; "src"; "A"; "A.fsproj" ] ] }

    match OutputSafety.validate outputDir partition with
    | Ok() -> failwith "expected validation to fail"
    | Error message ->
        Assert.Contains("output directory", message)
        // The message must name the offending directory so the user can act on it.
        Assert.Contains(outputDir, message)
```

- [x] **Step 2: Run the tests to verify they fail**

Run: `cd src/DotnetIsolate && dotnet test DotnetIsolate.UnitTests/DotnetIsolate.UnitTests.fsproj`
Expected: compile error — `OutputSafety` is not defined.

- [x] **Step 3: Write the implementation**

Create `src/DotnetIsolate/DotnetIsolate.Core/OutputSafety.fs`:

```fsharp
module DotnetIsolate.Core.OutputSafety

/// The resolved input set split by whether each file lives under the output directory.
type InputPartition =
    { /// Files that are genuine inputs and will be materialized.
      Kept: string list
      /// Files that sit under the output directory - almost always a previous run's output that
      /// MSBuild's implicit globs swept back up. Reported so a genuine source file living there
      /// doesn't vanish silently.
      ExcludedUnderOutput: string list }

/// Splits `files` on whether each one lives under `outputDir`.
///
/// This exists because a previous run's output is indistinguishable, to MSBuild, from source: the
/// SDK's default globs happily collect `out/**/*.cs` as Compile items of the project that contains
/// `out/`. Materializing those would place the output inside itself, one level deeper each run.
/// Uses MirrorRoot.isUnder, which compares path segments case-insensitively, so "out2" is never
/// mistaken for a child of "out".
let partitionInputs (outputDir: string) (files: string list) : InputPartition =
    let under, outside = files |> List.partition (MirrorRoot.isUnder outputDir)

    { Kept = outside
      ExcludedUnderOutput = under }

/// Fails when the output directory would consume its own inputs - i.e. it equals, or is an
/// ancestor of, every resolved file, so nothing survives `partitionInputs`.
///
/// This is the guard against the destructive case: pointing `-o` at the solution root previously
/// deleted the entire source tree (FR-7's delete-and-recreate) and only then failed. Validation
/// runs before any filesystem mutation.
let validate (outputDir: string) (partition: InputPartition) : Result<unit, string> =
    if List.isEmpty partition.Kept then
        Error
            $"the output directory {outputDir} contains every resolved input file, so it would consume its own inputs; choose an output directory outside the source tree"
    else
        Ok()
```

- [x] **Step 4: Register both files in their projects**

In `DotnetIsolate.Core.fsproj`, add after the `MirrorRoot.fs` line (it depends on `MirrorRoot`):

```xml
        <Compile Include="OutputSafety.fs"/>
```

In `DotnetIsolate.UnitTests.fsproj`, add after the `MirrorRootTests.fs` line:

```xml
        <Compile Include="OutputSafetyTests.fs"/>
```

- [x] **Step 5: Run the tests to verify they pass**

Run: `cd src/DotnetIsolate && dotnet test DotnetIsolate.UnitTests/DotnetIsolate.UnitTests.fsproj`
Expected: PASS, all 7 new tests green.

- [x] **Step 6: Commit**

```bash
git add src/DotnetIsolate/DotnetIsolate.Core/OutputSafety.fs \
        src/DotnetIsolate/DotnetIsolate.Core/DotnetIsolate.Core.fsproj \
        src/DotnetIsolate/DotnetIsolate.UnitTests/OutputSafetyTests.fs \
        src/DotnetIsolate/DotnetIsolate.UnitTests/DotnetIsolate.UnitTests.fsproj
git commit -m "feat: add OutputSafety to partition and validate inputs against the output dir"
```

---

### Task 2: `Materialize` — stop deleting by default, report stale entries

**Files:**
- Modify: `src/DotnetIsolate/DotnetIsolate.Core/Materialize.fs:25-33`
- Modify: `src/DotnetIsolate/DotnetIsolate.IntegrationTests/MaterializeTests.fs`

**Interfaces:**
- Consumes: `LinkStrategy.Strategy` (`Hardlink | Copy`), `Hardlink.create`.
- Produces: `Materialize.materialize : strategy:LinkStrategy.Strategy -> clean:bool -> mirrorRoot:string -> outputRoot:string -> files:string list -> string list` — returns the absolute paths of entries that were already in `outputRoot` and were **not** produced by this run. Always `[]` when `clean` is true.

- [x] **Step 1: Write the failing tests**

In `src/DotnetIsolate/DotnetIsolate.IntegrationTests/MaterializeTests.fs`, **replace** the existing test `` `materialize deletes and recreates a pre-existing output folder (FR-7)` `` (lines 49-60) with these three, and update the two earlier tests' calls to pass `false` for `clean` (i.e. `Materialize.materialize LinkStrategy.Hardlink false mirrorRoot outputRoot files`):

```fsharp
// Supersedes the old FR-7 delete-and-recreate default: an unrelated pre-existing file survives,
// because the destructive default could (and did) delete a user's source tree.
[<Fact>]
let ``materialize preserves pre-existing output content and reports it as stale`` () =
    withTempDir (fun root ->
        let mirrorRoot = Path.Combine(root, "mirror")
        let outputRoot = Path.Combine(root, "output")
        writeFile (Path.Combine(mirrorRoot, "A.fsproj")) "current"
        writeFile (Path.Combine(outputRoot, "stale-leftover.txt")) "from a previous run"

        let stale =
            Materialize.materialize LinkStrategy.Copy false mirrorRoot outputRoot [ Path.Combine(mirrorRoot, "A.fsproj") ]

        Assert.True(File.Exists(Path.Combine(outputRoot, "stale-leftover.txt")))
        Assert.True(File.Exists(Path.Combine(outputRoot, "A.fsproj")))
        Assert.Equal(1, stale.Length)
        Assert.Equal("stale-leftover.txt", Path.GetFileName(List.head stale)))

[<Fact>]
let ``materialize does not report a file this run produced as stale`` () =
    withTempDir (fun root ->
        let mirrorRoot = Path.Combine(root, "mirror")
        let outputRoot = Path.Combine(root, "output")
        writeFile (Path.Combine(mirrorRoot, "A.fsproj")) "current"
        // Same relative path this run will write, left over from a previous run.
        writeFile (Path.Combine(outputRoot, "A.fsproj")) "previous"

        let stale =
            Materialize.materialize LinkStrategy.Copy false mirrorRoot outputRoot [ Path.Combine(mirrorRoot, "A.fsproj") ]

        Assert.Empty(stale)
        Assert.Equal("current", File.ReadAllText(Path.Combine(outputRoot, "A.fsproj"))))

[<Fact>]
let ``materialize with clean empties the output folder first`` () =
    withTempDir (fun root ->
        let mirrorRoot = Path.Combine(root, "mirror")
        let outputRoot = Path.Combine(root, "output")
        writeFile (Path.Combine(mirrorRoot, "A.fsproj")) "current"
        writeFile (Path.Combine(outputRoot, "stale-leftover.txt")) "from a previous run"

        let stale =
            Materialize.materialize LinkStrategy.Copy true mirrorRoot outputRoot [ Path.Combine(mirrorRoot, "A.fsproj") ]

        Assert.False(File.Exists(Path.Combine(outputRoot, "stale-leftover.txt")))
        Assert.True(File.Exists(Path.Combine(outputRoot, "A.fsproj")))
        Assert.Empty(stale))
```

- [x] **Step 2: Run the tests to verify they fail**

Run: `cd src/DotnetIsolate && dotnet test DotnetIsolate.IntegrationTests/DotnetIsolate.IntegrationTests.fsproj --filter "FullyQualifiedName~MaterializeTests"`
Expected: compile error — `materialize` takes 4 arguments, not 5.

- [x] **Step 3: Write the implementation**

Replace `materialize` in `src/DotnetIsolate/DotnetIsolate.Core/Materialize.fs` (keep `placeFile` unchanged):

```fsharp
/// Places every file in `files` (absolute paths under `mirrorRoot`) at its mirrored relative
/// location under `outputRoot`, using `strategy` (pipeline step 6).
///
/// `clean` selects between the two output-directory policies. The default (`false`) merges into
/// whatever is already there and returns the entries it found that this run did not produce, so
/// the caller can warn about them; it never deletes. `true` restores the original
/// delete-and-recreate behaviour for callers who need the output to contain exactly the isolated
/// set. Merging is the default because the destructive one, applied to an output directory that
/// overlapped the source tree, deleted the user's source before failing.
let materialize
    (strategy: LinkStrategy.Strategy)
    (clean: bool)
    (mirrorRoot: string)
    (outputRoot: string)
    (files: string list)
    : string list =
    if clean && Directory.Exists(outputRoot) then
        Directory.Delete(outputRoot, recursive = true)

    // Snapshot before placing anything, so files this run writes are never mistaken for stale.
    let preExisting =
        if Directory.Exists(outputRoot) then
            Directory.EnumerateFiles(outputRoot, "*", SearchOption.AllDirectories) |> Set.ofSeq
        else
            Set.empty

    Directory.CreateDirectory(outputRoot) |> ignore
    files |> List.iter (placeFile strategy mirrorRoot outputRoot)

    let produced =
        files
        |> List.map (fun f -> Path.Combine(outputRoot, Path.GetRelativePath(mirrorRoot, f)))
        |> Set.ofList

    Set.difference preExisting produced |> Set.toList
```

- [x] **Step 4: Fix the one other caller so the solution compiles**

`Pipeline.fs:100` calls `Materialize.materialize strategy mirrorRoot outputDir allFiles`. Change it to `Materialize.materialize strategy false mirrorRoot outputDir allFiles |> ignore` for now; Task 3 replaces this line properly.

- [x] **Step 5: Run the tests to verify they pass**

Run: `cd src/DotnetIsolate && dotnet test DotnetIsolate.IntegrationTests/DotnetIsolate.IntegrationTests.fsproj --filter "FullyQualifiedName~MaterializeTests"`
Expected: PASS.

- [x] **Step 6: Commit**

```bash
git add src/DotnetIsolate/DotnetIsolate.Core/Materialize.fs \
        src/DotnetIsolate/DotnetIsolate.Core/Pipeline.fs \
        src/DotnetIsolate/DotnetIsolate.IntegrationTests/MaterializeTests.fs
git commit -m "feat!: make materialize merge by default and report stale output entries"
```

---

### Task 3: Wire output-directory safety into the pipeline and add `--clean`

**Files:**
- Modify: `src/DotnetIsolate/DotnetIsolate.Core/Pipeline.fs`
- Modify: `src/DotnetIsolate/DotnetIsolate/Program.fs`
- Modify: `src/DotnetIsolate/DotnetIsolate.IntegrationTests/PipelineTests.fs`

**Interfaces:**
- Consumes: `OutputSafety.partitionInputs`, `OutputSafety.validate`, `Materialize.materialize` (5 args, returns stale list).
- Produces:
  - `Pipeline.IsolateOptions` gains `Clean: bool`.
  - `Pipeline.IsolateResult` gains `ExcludedUnderOutput: string list` and `StaleEntries: string list`.

Note: every existing construction of `IsolateOptions` in the test suites must gain `Clean = false`. Find them with `grep -rn "SolutionPath = " src/DotnetIsolate --include=*.fs`.

- [x] **Step 1: Write the failing tests**

Append to `src/DotnetIsolate/DotnetIsolate.IntegrationTests/PipelineTests.fs`:

```fsharp
// The destructive reproduction: -o pointing at the solution root used to delete the whole source
// tree and only then fail. It must now fail before touching anything.
[<Fact>]
let ``isolate refuses an output directory that contains every input, leaving the source intact`` () =
    withTempDir (fun root ->
        writeProject (Path.Combine(root, "A")) "A" [] []
        writeSolution (Path.Combine(root, "Fixture.sln")) [ "A", "A/A.fsproj" ]

        let ex =
            Assert.ThrowsAny<exn>(fun () ->
                Pipeline.isolate
                    { ProjectPaths = [ Path.Combine(root, "A", "A.fsproj") ]
                      OutputDir = Some root
                      SolutionPath = None
                      RestoreOnly = false
                      Clean = false }
                |> ignore)

        Assert.Contains("output directory", ex.Message)
        // The source tree must still be there - this is the data-loss regression guard.
        Assert.True(File.Exists(Path.Combine(root, "A", "A.fsproj")))
        Assert.True(File.Exists(Path.Combine(root, "Fixture.sln"))))

// The re-ingestion reproduction: an output directory nested inside a project directory is swept
// up by the SDK's default globs on the second run.
[<Fact>]
let ``isolate succeeds twice with an output directory nested inside a project directory`` () =
    withTempDir (fun root ->
        writeProject (Path.Combine(root, "A")) "A" [] []
        writeSolution (Path.Combine(root, "Fixture.sln")) [ "A", "A/A.fsproj" ]

        let outputDir = Path.Combine(root, "A", "out")

        let options =
            { ProjectPaths = [ Path.Combine(root, "A", "A.fsproj") ]
              OutputDir = Some outputDir
              SolutionPath = None
              RestoreOnly = false
              Clean = false }

        Pipeline.isolate options |> ignore
        let second = Pipeline.isolate options

        Assert.True(File.Exists(Path.Combine(outputDir, "A", "A.fsproj")))
        // Nothing was placed inside itself a second level down.
        Assert.False(Directory.Exists(Path.Combine(outputDir, "A", "out")))
        Assert.NotEmpty(second.ExcludedUnderOutput))

[<Fact>]
let ``isolate reports pre-existing output entries as stale rather than deleting them`` () =
    withTempDir (fun root ->
        writeProject (Path.Combine(root, "A")) "A" [] []
        writeSolution (Path.Combine(root, "Fixture.sln")) [ "A", "A/A.fsproj" ]

        let outputDir = Path.Combine(root, "output")
        Directory.CreateDirectory(outputDir) |> ignore
        File.WriteAllText(Path.Combine(outputDir, "leftover.txt"), "previous run")

        let result =
            Pipeline.isolate
                { ProjectPaths = [ Path.Combine(root, "A", "A.fsproj") ]
                  OutputDir = Some outputDir
                  SolutionPath = None
                  RestoreOnly = false
                  Clean = false }

        Assert.True(File.Exists(Path.Combine(outputDir, "leftover.txt")))
        Assert.Contains(result.StaleEntries, fun e -> Path.GetFileName(e) = "leftover.txt"))

[<Fact>]
let ``isolate with Clean removes pre-existing output entries`` () =
    withTempDir (fun root ->
        writeProject (Path.Combine(root, "A")) "A" [] []
        writeSolution (Path.Combine(root, "Fixture.sln")) [ "A", "A/A.fsproj" ]

        let outputDir = Path.Combine(root, "output")
        Directory.CreateDirectory(outputDir) |> ignore
        File.WriteAllText(Path.Combine(outputDir, "leftover.txt"), "previous run")

        Pipeline.isolate
            { ProjectPaths = [ Path.Combine(root, "A", "A.fsproj") ]
              OutputDir = Some outputDir
              SolutionPath = None
              RestoreOnly = false
              Clean = true }
        |> ignore

        Assert.False(File.Exists(Path.Combine(outputDir, "leftover.txt"))))
```

These reference `ProjectPaths` and `RestoreOnly`, which Tasks 6-9 introduce. To keep this task independently testable, add **all** the new `IsolateOptions` fields now (`ProjectPaths`, `RestoreOnly`, `Clean`) but only implement `Clean` and the safety wiring in this task; `ProjectPaths` is consumed as `List.head` for now and `RestoreOnly` is ignored. Tasks 7 and 9 give them their real behaviour.

- [x] **Step 2: Run the tests to verify they fail**

Run: `cd src/DotnetIsolate && dotnet test DotnetIsolate.IntegrationTests/DotnetIsolate.IntegrationTests.fsproj --filter "FullyQualifiedName~PipelineTests"`
Expected: compile error — `IsolateOptions` has no field `ProjectPaths`.

- [x] **Step 3: Update the option and result types**

In `Pipeline.fs`, replace the two type definitions:

```fsharp
type IsolateOptions =
    { /// One or more entry projects; their dependency closures are unioned.
      ProjectPaths: string list
      OutputDir: string option
      SolutionPath: string option
      /// Emit only the files `dotnet restore` needs (see Phase.fs), rather than the full set.
      RestoreOnly: bool
      /// Delete and recreate the output directory first, instead of merging into it.
      Clean: bool }

type IsolateResult =
    { OutputDir: string
      IncludedProjects: string list
      FileCount: int
      SolutionRoot: SolutionDiscovery.SolutionRoot option
      Strategy: LinkStrategy.Strategy
      /// Resolved inputs dropped because they live under the output directory - almost always a
      /// previous run's output swept up by MSBuild's implicit globs.
      ExcludedUnderOutput: string list
      /// Entries already in the output directory that this run did not produce.
      StaleEntries: string list }
```

- [x] **Step 4: Wire safety into `isolate`**

In `Pipeline.fs`, take the entry project as `let projectPath = Path.GetFullPath(List.head options.ProjectPaths)` for now.

Move the output-directory computation **above** the mirror-root computation (it currently sits at lines 90-94, after it), because the partition must happen before the mirror root is derived — otherwise an excluded file still inflates the root:

```fsharp
    // FR-6: output defaults to ./<ProjectName>, or an explicit -o/--output-dir path.
    let outputDir =
        match options.OutputDir with
        | Some dir -> Path.GetFullPath(dir)
        | None -> Path.GetFullPath(Path.GetFileNameWithoutExtension(projectPath))

    // A previous run's output is indistinguishable from source to MSBuild's implicit globs, so
    // drop anything under the output directory before it can inflate the mirror root or get
    // placed inside itself.
    let partition = OutputSafety.partitionInputs outputDir allFiles

    match OutputSafety.validate outputDir partition with
    | Error message -> failwith message
    | Ok() -> ()

    let allFiles = partition.Kept
```

Then the existing `if List.isEmpty allFiles then failwith ...` check and the mirror-root computation follow, both operating on the partitioned `allFiles`.

Change the materialize call to capture stale entries:

```fsharp
    let staleEntries = Materialize.materialize strategy options.Clean mirrorRoot outputDir allFiles
```

And extend the returned record:

```fsharp
      ExcludedUnderOutput = partition.ExcludedUnderOutput
      StaleEntries = staleEntries }
```

- [x] **Step 5: Add the CLI flags**

In `src/DotnetIsolate/DotnetIsolate/Program.fs`, add to `Arguments` and its `Usage`:

```fsharp
    | Clean
```

```fsharp
            | Clean _ -> "delete and recreate the output directory instead of merging into it"
```

Build the options with the new fields and report the new diagnostics after the existing output:

```fsharp
        let result =
            Pipeline.isolate
                { ProjectPaths = [ results.GetResult(ProjectPath) ]
                  OutputDir = results.TryGetResult(Output_Dir)
                  SolutionPath = results.TryGetResult(Solution)
                  RestoreOnly = false
                  Clean = results.Contains(Clean) }
```

```fsharp
        for entry in result.ExcludedUnderOutput do
            eprintfn $"warning: ignoring {entry}, which lives under the output directory"

        for entry in result.StaleEntries do
            eprintfn $"warning: {entry} was already in the output directory and was not produced by this run"
```

- [x] **Step 6: Fix every other `IsolateOptions` construction**

Run `grep -rn "SolutionPath = " src/DotnetIsolate --include=*.fs` and add `ProjectPaths` (replacing `ProjectPath`), `RestoreOnly = false` and `Clean = false` to each. `DotnetIsolate.E2ETests/DockerCacheTests.fs:87` and `:98` are among them.

- [x] **Step 7: Run the full build and both test suites**

Run: `cd src/DotnetIsolate && dotnet build DotnetIsolate.sln && dotnet test DotnetIsolate.UnitTests/DotnetIsolate.UnitTests.fsproj && dotnet test DotnetIsolate.IntegrationTests/DotnetIsolate.IntegrationTests.fsproj`
Expected: PASS.

- [x] **Step 8: Commit**

```bash
git add -A src/DotnetIsolate
git commit -m "feat!: refuse an output dir that consumes its inputs, add --clean"
```

---

### Task 4: `FileResolution` — drop and report missing item-derived files

**Files:**
- Modify: `src/DotnetIsolate/DotnetIsolate.Core/FileResolution.fs:100-119`
- Modify: `src/DotnetIsolate/DotnetIsolate.UnitTests/FileResolutionTests.fs`

**Interfaces:**
- Produces:
  - `type FileResolution.ResolvedFiles = { Files: string list; MissingItemFiles: string list }`
  - `FileResolution.resolveFiles : Resolvers -> projectPath:string -> ResolvedFiles`
  - `FileResolution.resolveAllFiles : Resolvers -> projects:string list -> ResolvedFiles`

- [x] **Step 1: Write the failing tests**

Append to `src/DotnetIsolate/DotnetIsolate.UnitTests/FileResolutionTests.fs` (match the file's existing helper style for building a `Resolvers` value; if it builds one inline, do the same here):

```fsharp
// The .dockerignore reproduction: a <None Include="..\.dockerignore"/> whose target was itself
// excluded from the Docker build context. MSBuild reports the item regardless - item resolution
// never consults the disk - and materialization then died copying a file that isn't there.
[<Fact>]
let ``resolveFiles drops a missing item-derived file and reports it`` () =
    let projectPath = path [ "repo"; "A"; "A.fsproj" ]
    let present = path [ "repo"; "A"; "Program.fs" ]
    let missing = path [ "repo"; ".dockerignore" ]

    let resolvers =
        { GetItems = fun _ -> Map.ofList [ "Compile", [ present ]; "None", [ missing ] ]
          GetProperties = fun _ -> Map.empty
          FileExists = fun p -> p <> missing
          AncestorCeiling = None }

    let result = FileResolution.resolveFiles resolvers projectPath

    Assert.Contains(present, result.Files)
    Assert.DoesNotContain(missing, result.Files)
    Assert.Equal<string list>([ missing ], result.MissingItemFiles)

// The project file itself is never existence-checked: MSBuild just evaluated it.
[<Fact>]
let ``resolveFiles always includes the project file`` () =
    let projectPath = path [ "repo"; "A"; "A.fsproj" ]

    let resolvers =
        { GetItems = fun _ -> Map.empty
          GetProperties = fun _ -> Map.empty
          FileExists = fun _ -> false
          AncestorCeiling = None }

    let result = FileResolution.resolveFiles resolvers projectPath

    Assert.Contains(projectPath, result.Files)

// Property paths are scalars the SDK routinely defaults to paths never meant to exist, so they
// are dropped silently - warning on them would be noise on projects that build fine.
[<Fact>]
let ``resolveFiles drops a missing property-derived file without reporting it`` () =
    let projectPath = path [ "repo"; "A"; "A.fsproj" ]
    let missingRuleset = path [ "repo"; "A"; "analysis.ruleset" ]

    let resolvers =
        { GetItems = fun _ -> Map.empty
          GetProperties = fun _ -> Map.ofList [ "CodeAnalysisRuleSet", "analysis.ruleset" ]
          FileExists = fun _ -> false
          AncestorCeiling = None }

    let result = FileResolution.resolveFiles resolvers projectPath

    Assert.DoesNotContain(missingRuleset, result.Files)
    Assert.Empty(result.MissingItemFiles)

[<Fact>]
let ``resolveAllFiles unions files and missing reports across projects`` () =
    let projectA = path [ "repo"; "A"; "A.fsproj" ]
    let projectB = path [ "repo"; "B"; "B.fsproj" ]
    let missing = path [ "repo"; ".dockerignore" ]

    let resolvers =
        { GetItems = fun _ -> Map.ofList [ "None", [ missing ] ]
          GetProperties = fun _ -> Map.empty
          FileExists = fun p -> p <> missing
          AncestorCeiling = None }

    let result = FileResolution.resolveAllFiles resolvers [ projectA; projectB ]

    Assert.Contains(projectA, result.Files)
    Assert.Contains(projectB, result.Files)
    // Deduplicated: both projects reported the same missing file.
    Assert.Equal<string list>([ missing ], result.MissingItemFiles)
```

- [x] **Step 2: Run the tests to verify they fail**

Run: `cd src/DotnetIsolate && dotnet test DotnetIsolate.UnitTests/DotnetIsolate.UnitTests.fsproj --filter "FullyQualifiedName~FileResolutionTests"`
Expected: compile error — `resolveFiles` returns `string list`, which has no `Files` member.

- [x] **Step 3: Write the implementation**

Replace `resolveFiles` and `resolveAllFiles` in `FileResolution.fs`:

```fsharp
/// A project's resolved files, plus the item-derived paths that were dropped because nothing is
/// there on disk.
type ResolvedFiles =
    { Files: string list
      /// Item-derived paths that do not exist. Worth reporting: an item was authored by someone,
      /// so its absence is a fact about the tree the user may want to know - as when a
      /// `<None Include="..\.dockerignore"/>` target is excluded from the Docker build context.
      MissingItemFiles: string list }

/// Resolves the deduplicated, flat set of build-relevant file paths for a single project (FR-4,
/// FR-9) - including the project file itself, which `dotnet msbuild -getItem` never returns (a
/// .fsproj/.csproj isn't a Compile/Content/None/... item of itself), but which is obviously needed
/// to build the isolated output at all.
///
/// Item-derived paths are existence-checked, because MSBuild resolves items without consulting the
/// disk: a perfectly ordinary project can name a file that isn't there, and copying it would fail
/// the whole run. Dropping it here also keeps it out of the mirror-root computation, so an absent
/// `..\.dockerignore` no longer adds a level to every path in the output.
let resolveFiles (resolvers: Resolvers) (projectPath: string) : ResolvedFiles =
    let items = resolvers.GetItems projectPath

    let withinCeiling =
        match resolvers.AncestorCeiling with
        | Some ceiling -> List.filter (MirrorRoot.isUnder ceiling)
        | None -> id

    let ancestorGlobbedFiles = itemsOfTypes ancestorGlobbedItemTypes items |> withinCeiling

    let itemFiles =
        (itemsOfTypes fileItemTypes items @ ancestorGlobbedFiles) |> List.distinct

    let presentItemFiles, missingItemFiles = itemFiles |> List.partition resolvers.FileExists

    let propertyFiles =
        resolvePropertyFiles resolvers.FileExists (resolvers.GetProperties projectPath) projectPath

    { Files = projectPath :: (presentItemFiles @ propertyFiles) |> List.distinct
      MissingItemFiles = missingItemFiles }

/// Resolves the deduplicated, flat set of build-relevant file paths across every project in
/// `projects` (pipeline step 2 in DESIGN.md) - e.g. the full set returned by ProjectGraph.resolve.
let resolveAllFiles (resolvers: Resolvers) (projects: string list) : ResolvedFiles =
    let resolved = projects |> List.map (resolveFiles resolvers)

    { Files = resolved |> List.collect (fun r -> r.Files) |> List.distinct
      MissingItemFiles = resolved |> List.collect (fun r -> r.MissingItemFiles) |> List.distinct }
```

- [x] **Step 4: Update the pipeline caller**

In `Pipeline.fs`, `let projectFiles = FileResolution.resolveAllFiles resolvers projects` becomes:

```fsharp
    let resolvedProjectFiles = FileResolution.resolveAllFiles resolvers projects
    let projectFiles = resolvedProjectFiles.Files
```

Add `MissingFiles: string list` to `IsolateResult` and set it to `resolvedProjectFiles.MissingItemFiles`.

In `Program.fs`, report them:

```fsharp
        for entry in result.MissingFiles do
            eprintfn $"warning: {entry} is referenced by a project but does not exist; skipped"
```

- [x] **Step 5: Run the tests to verify they pass**

Run: `cd src/DotnetIsolate && dotnet build DotnetIsolate.sln && dotnet test DotnetIsolate.UnitTests/DotnetIsolate.UnitTests.fsproj`
Expected: PASS. Fix any integration test that asserted on the old `string list` return.

- [x] **Step 6: Add the integration reproduction**

Append to `src/DotnetIsolate/DotnetIsolate.IntegrationTests/PipelineTests.fs`:

```fsharp
[<Fact>]
let ``isolate warns about a referenced file that does not exist instead of failing`` () =
    withTempDir (fun root ->
        writeProject (Path.Combine(root, "A")) "A" [] []
        writeSolution (Path.Combine(root, "Fixture.sln")) [ "A", "A/A.fsproj" ]

        // Reference a file that is never created - the .dockerignore reproduction.
        let projectFile = Path.Combine(root, "A", "A.fsproj")
        let content = File.ReadAllText(projectFile)

        File.WriteAllText(
            projectFile,
            content.Replace("</Project>", "<ItemGroup><None Include=\"absent.txt\"/></ItemGroup></Project>")
        )

        let result =
            Pipeline.isolate
                { ProjectPaths = [ projectFile ]
                  OutputDir = Some(Path.Combine(root, "output"))
                  SolutionPath = None
                  RestoreOnly = false
                  Clean = false }

        Assert.Contains(result.MissingFiles, fun f -> Path.GetFileName(f) = "absent.txt"))
```

Run: `cd src/DotnetIsolate && dotnet test DotnetIsolate.IntegrationTests/DotnetIsolate.IntegrationTests.fsproj --filter "FullyQualifiedName~PipelineTests"`
Expected: PASS.

- [x] **Step 7: Commit**

```bash
git add -A src/DotnetIsolate
git commit -m "feat!: skip and report referenced files that do not exist on disk"
```

---

### Task 5: `ProjectGraph.resolveMany`

**Files:**
- Modify: `src/DotnetIsolate/DotnetIsolate.Core/ProjectGraph.fs:16-34`
- Modify: `src/DotnetIsolate/DotnetIsolate.UnitTests/ProjectGraphTests.fs`

**Interfaces:**
- Produces: `ProjectGraph.resolveMany : ProjectReferenceResolver -> entryProjects:string list -> string list`. `resolve` is retained as a single-entry wrapper so existing callers and tests are unaffected.

- [x] **Step 1: Write the failing tests**

Append to `src/DotnetIsolate/DotnetIsolate.UnitTests/ProjectGraphTests.fs`:

```fsharp
// Two entry projects sharing a dependency: the union, with the shared project appearing once.
[<Fact>]
let ``resolveMany returns the deduplicated union of two overlapping closures`` () =
    let login = path [ "repo"; "Login"; "Login.fsproj" ]
    let loginQs = path [ "repo"; "LoginQs"; "LoginQs.fsproj" ]
    let shared = path [ "repo"; "Shared"; "Shared.fsproj" ]

    let references p =
        if p = login then [ shared ]
        elif p = loginQs then [ shared ]
        else []

    let result = ProjectGraph.resolveMany references [ login; loginQs ]

    Assert.Equal(3, result.Length)
    Assert.Contains(login, result)
    Assert.Contains(loginQs, result)
    Assert.Contains(shared, result)

[<Fact>]
let ``resolveMany with a single entry matches resolve`` () =
    let a = path [ "repo"; "A"; "A.fsproj" ]
    let b = path [ "repo"; "B"; "B.fsproj" ]
    let references p = if p = a then [ b ] else []

    Assert.Equal<string list>(ProjectGraph.resolve references a, ProjectGraph.resolveMany references [ a ])
```

- [x] **Step 2: Run the tests to verify they fail**

Run: `cd src/DotnetIsolate && dotnet test DotnetIsolate.UnitTests/DotnetIsolate.UnitTests.fsproj --filter "FullyQualifiedName~ProjectGraphTests"`
Expected: compile error — `resolveMany` is not defined.

- [x] **Step 3: Write the implementation**

In `ProjectGraph.fs`, rename `resolve` to `resolveMany`, change its seed frontier from `[ entryProject ]` to the deduplicated `entryProjects`, and add the wrapper. Keep the existing doc comment on `resolveMany` (it explains the parallel-per-level design and PR-1) and add a sentence about multiple entries:

```fsharp
/// ...existing doc comment...
///
/// Several entry projects are seeded into the first BFS level together, so a project reachable
/// from more than one of them is still visited once and the result is their unioned closure.
let resolveMany (getReferences: ProjectReferenceResolver) (entryProjects: string list) : string list =
    let rec go (visited: Set<string>) (frontier: string list) (levels: string list list) : string list list =
        match frontier with
        | [] -> levels
        | _ ->
            let visited = frontier |> List.fold (fun v p -> Set.add p v) visited

            let nextFrontier =
                frontier
                |> Array.ofList
                |> Array.Parallel.map getReferences
                |> Array.toList
                |> List.concat
                |> List.distinct
                |> List.filter (fun p -> not (Set.contains p visited))

            go visited nextFrontier (frontier :: levels)

    go Set.empty (List.distinct entryProjects) [] |> List.rev |> List.concat

/// Single-entry `resolveMany`.
let resolve (getReferences: ProjectReferenceResolver) (entryProject: string) : string list =
    resolveMany getReferences [ entryProject ]
```

- [x] **Step 4: Run the tests to verify they pass**

Run: `cd src/DotnetIsolate && dotnet test DotnetIsolate.UnitTests/DotnetIsolate.UnitTests.fsproj --filter "FullyQualifiedName~ProjectGraphTests"`
Expected: PASS.

- [x] **Step 5: Commit**

```bash
git add src/DotnetIsolate/DotnetIsolate.Core/ProjectGraph.fs \
        src/DotnetIsolate/DotnetIsolate.UnitTests/ProjectGraphTests.fs
git commit -m "feat: add ProjectGraph.resolveMany for multiple entry projects"
```

---

### Task 6: Multiple entry projects end to end

**Files:**
- Modify: `src/DotnetIsolate/DotnetIsolate.Core/Pipeline.fs`
- Modify: `src/DotnetIsolate/DotnetIsolate/Program.fs`
- Modify: `src/DotnetIsolate/DotnetIsolate.IntegrationTests/PipelineTests.fs`

**Interfaces:**
- Consumes: `ProjectGraph.resolveMany`, `Pipeline.IsolateOptions.ProjectPaths`.
- Produces: no new public names; `Pipeline.isolate` now honours every entry in `ProjectPaths`.

- [x] **Step 1: Write the failing tests**

Append to `PipelineTests.fs`:

```fsharp
// The login + login.qs shape: two entry services sharing a dependency, isolated into one tree.
[<Fact>]
let ``isolate unions the closures of two entry projects`` () =
    withTempDir (fun root ->
        writeProject (Path.Combine(root, "Login")) "Login" [ "Shared" ] []
        writeProject (Path.Combine(root, "LoginQs")) "LoginQs" [ "Shared" ] []
        writeProject (Path.Combine(root, "Shared")) "Shared" [] []
        writeProject (Path.Combine(root, "Unrelated")) "Unrelated" [] []

        writeSolution
            (Path.Combine(root, "Fixture.sln"))
            [ "Login", "Login/Login.fsproj"
              "LoginQs", "LoginQs/LoginQs.fsproj"
              "Shared", "Shared/Shared.fsproj"
              "Unrelated", "Unrelated/Unrelated.fsproj" ]

        let outputDir = Path.Combine(root, "output")

        let result =
            Pipeline.isolate
                { ProjectPaths =
                    [ Path.Combine(root, "Login", "Login.fsproj")
                      Path.Combine(root, "LoginQs", "LoginQs.fsproj") ]
                  OutputDir = Some outputDir
                  SolutionPath = None
                  RestoreOnly = false
                  Clean = false }

        Assert.True(File.Exists(Path.Combine(outputDir, "Login", "Login.fsproj")))
        Assert.True(File.Exists(Path.Combine(outputDir, "LoginQs", "LoginQs.fsproj")))
        Assert.True(File.Exists(Path.Combine(outputDir, "Shared", "Shared.fsproj")))
        // The unrelated project is still excluded - that is the whole point of the tool.
        Assert.False(File.Exists(Path.Combine(outputDir, "Unrelated", "Unrelated.fsproj")))
        Assert.Equal(3, result.IncludedProjects.Length))

[<Fact>]
let ``isolate requires an explicit output directory for more than one entry project`` () =
    withTempDir (fun root ->
        writeProject (Path.Combine(root, "A")) "A" [] []
        writeProject (Path.Combine(root, "B")) "B" [] []
        writeSolution (Path.Combine(root, "Fixture.sln")) [ "A", "A/A.fsproj"; "B", "B/B.fsproj" ]

        let ex =
            Assert.ThrowsAny<exn>(fun () ->
                Pipeline.isolate
                    { ProjectPaths =
                        [ Path.Combine(root, "A", "A.fsproj"); Path.Combine(root, "B", "B.fsproj") ]
                      OutputDir = None
                      SolutionPath = None
                      RestoreOnly = false
                      Clean = false }
                |> ignore)

        Assert.Contains("output directory", ex.Message))
```

- [x] **Step 2: Run the tests to verify they fail**

Run: `cd src/DotnetIsolate && dotnet test DotnetIsolate.IntegrationTests/DotnetIsolate.IntegrationTests.fsproj --filter "FullyQualifiedName~PipelineTests"`
Expected: FAIL — only the first entry project's closure is isolated.

- [x] **Step 3: Write the implementation**

In `Pipeline.fs`:

```fsharp
    let projectPaths = options.ProjectPaths |> List.map Path.GetFullPath

    if List.isEmpty projectPaths then
        failwith "at least one project path is required"

    // Solution discovery walks up from an entry project's directory; with several entries any of
    // them finds the same solution, so the first is used and the others are validated against the
    // graph it scopes.
    let projectDir = Path.GetDirectoryName(List.head projectPaths)
```

Replace the graph resolution:

```fsharp
    let projects = ProjectGraph.resolveMany projectReferenceResolver projectPaths
```

Replace the output-directory default:

```fsharp
    let outputDir =
        match options.OutputDir, projectPaths with
        | Some dir, _ -> Path.GetFullPath(dir)
        | None, [ single ] -> Path.GetFullPath(Path.GetFileNameWithoutExtension(single))
        | None, _ ->
            failwith
                "an explicit output directory (-o/--output-dir) is required when more than one entry project is given"
```

- [x] **Step 4: Update the CLI to accept a list**

In `Program.fs`, change the positional argument and its usage:

```fsharp
    | [<MainCommand; ExactlyOnce>] Project_Paths of paths: string list
```

```fsharp
            | Project_Paths _ -> "one or more .csproj/.fsproj files to isolate"
```

and build the options with `ProjectPaths = results.GetResult(Project_Paths)`.

- [x] **Step 5: Run the tests to verify they pass**

Run: `cd src/DotnetIsolate && dotnet build DotnetIsolate.sln && dotnet test DotnetIsolate.IntegrationTests/DotnetIsolate.IntegrationTests.fsproj`
Expected: PASS.

- [x] **Step 6: Verify the CLI accepts two projects**

Run:

```bash
cd src/DotnetIsolate && dotnet run --project DotnetIsolate/DotnetIsolate.fsproj -- --help
```

Expected: usage shows `<paths>...` for the main command and lists `--restore` is *not* yet present (Task 8 adds it), `--clean` is.

- [x] **Step 7: Commit**

```bash
git add -A src/DotnetIsolate
git commit -m "feat: isolate the union of several entry projects in one run"
```

---

### Task 7: `Phase` — select the restore-relevant subset

**Files:**
- Create: `src/DotnetIsolate/DotnetIsolate.Core/Phase.fs`
- Create: `src/DotnetIsolate/DotnetIsolate.UnitTests/PhaseTests.fs`
- Modify: `src/DotnetIsolate/DotnetIsolate.Core/DotnetIsolate.Core.fsproj`
- Modify: `src/DotnetIsolate/DotnetIsolate.UnitTests/DotnetIsolate.UnitTests.fsproj`

**Interfaces:**
- Produces:
  - `Phase.restoreRelevantFileNames : string list`
  - `Phase.restoreSubset : projects:string list -> files:string list -> string list`

- [x] **Step 1: Write the failing tests**

Create `src/DotnetIsolate/DotnetIsolate.UnitTests/PhaseTests.fs`:

```fsharp
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
```

- [x] **Step 2: Run the tests to verify they fail**

Run: `cd src/DotnetIsolate && dotnet test DotnetIsolate.UnitTests/DotnetIsolate.UnitTests.fsproj`
Expected: compile error — `Phase` is not defined.

- [x] **Step 3: Write the implementation**

Create `src/DotnetIsolate/DotnetIsolate.Core/Phase.fs`:

```fsharp
module DotnetIsolate.Core.Phase

open System
open System.IO

/// The files `dotnet restore` evaluates, beyond the project and solution files themselves.
///
/// Deliberately *not* `ImplicitFilesIo.wellKnownFileNames`: that list includes `.editorconfig`,
/// which restore never reads. Including it would invalidate the cached Docker restore layer on
/// every formatting-rule edit, defeating the reason the restore phase exists.
///
/// `packages.lock.json` is here because locked-mode restore (`RestorePackagesWithLockFile`) fails
/// without it. It already reaches the resolved set through the SDK's default `None` glob, which
/// collects every otherwise-unmatched file in a project directory, so this only routes it.
let restoreRelevantFileNames =
    [ "Directory.Build.props"
      "Directory.Build.targets"
      "Directory.Packages.props"
      "NuGet.config"
      "global.json"
      "packages.lock.json" ]

/// Selects the subset of `files` that `dotnet restore` needs in order to evaluate `projects`: the
/// project files themselves, plus any file named in `restoreRelevantFileNames`.
///
/// The scoped solution file is not selected here because it is generated rather than resolved -
/// the pipeline writes it in both phases.
///
/// Name comparison is case-insensitive: "nuget.config" and "NuGet.config" are both common, and on
/// a case-sensitive filesystem they are different filenames for the same role.
let restoreSubset (projects: string list) (files: string list) : string list =
    let projectSet = projects |> Set.ofList

    let isRestoreRelevantName (file: string) =
        let name = Path.GetFileName(file)

        restoreRelevantFileNames
        |> List.exists (fun known -> String.Equals(name, known, StringComparison.OrdinalIgnoreCase))

    files
    |> List.filter (fun f -> Set.contains f projectSet || isRestoreRelevantName f)
```

- [x] **Step 4: Register both files in their projects**

In `DotnetIsolate.Core.fsproj`, add before `<Compile Include="Pipeline.fs"/>`:

```xml
        <Compile Include="Phase.fs"/>
```

In `DotnetIsolate.UnitTests.fsproj`, add after the `OutputSafetyTests.fs` line:

```xml
        <Compile Include="PhaseTests.fs"/>
```

- [x] **Step 5: Run the tests to verify they pass**

Run: `cd src/DotnetIsolate && dotnet test DotnetIsolate.UnitTests/DotnetIsolate.UnitTests.fsproj`
Expected: PASS.

- [x] **Step 6: Commit**

```bash
git add src/DotnetIsolate/DotnetIsolate.Core/Phase.fs \
        src/DotnetIsolate/DotnetIsolate.Core/DotnetIsolate.Core.fsproj \
        src/DotnetIsolate/DotnetIsolate.UnitTests/PhaseTests.fs \
        src/DotnetIsolate/DotnetIsolate.UnitTests/DotnetIsolate.UnitTests.fsproj
git commit -m "feat: add Phase.restoreSubset selecting the dotnet restore inputs"
```

---

### Task 8: Wire `--restore` into the pipeline and CLI

**Files:**
- Modify: `src/DotnetIsolate/DotnetIsolate.Core/Pipeline.fs`
- Modify: `src/DotnetIsolate/DotnetIsolate/Program.fs`
- Modify: `src/DotnetIsolate/DotnetIsolate.IntegrationTests/PipelineTests.fs`

**Interfaces:**
- Consumes: `Phase.restoreSubset`, `Pipeline.IsolateOptions.RestoreOnly`.
- Produces: `--restore` CLI flag; default output directory becomes `./<ProjectName>.restore` under `--restore`.

The subset is applied **after** the mirror root is computed from the full set, so both phases share one mirror root and the two outputs overlay exactly.

- [x] **Step 1: Write the failing tests**

Append to `PipelineTests.fs`:

```fsharp
[<Fact>]
let ``isolate with RestoreOnly emits project and build files but not sources`` () =
    withTempDir (fun root ->
        writeProject (Path.Combine(root, "A")) "A" [ "B" ] [ "appsettings.json" ]
        writeProject (Path.Combine(root, "B")) "B" [] []
        File.WriteAllText(Path.Combine(root, "NuGet.config"), "<configuration/>")
        File.WriteAllText(Path.Combine(root, "Directory.Packages.props"), "<Project/>")
        File.WriteAllText(Path.Combine(root, ".editorconfig"), "root = true")
        writeSolution (Path.Combine(root, "Fixture.sln")) [ "A", "A/A.fsproj"; "B", "B/B.fsproj" ]

        let outputDir = Path.Combine(root, "restore-output")

        Pipeline.isolate
            { ProjectPaths = [ Path.Combine(root, "A", "A.fsproj") ]
              OutputDir = Some outputDir
              SolutionPath = None
              RestoreOnly = true
              Clean = false }
        |> ignore

        // The restore inputs are present.
        Assert.True(File.Exists(Path.Combine(outputDir, "A", "A.fsproj")))
        Assert.True(File.Exists(Path.Combine(outputDir, "B", "B.fsproj")))
        Assert.True(File.Exists(Path.Combine(outputDir, "NuGet.config")))
        Assert.True(File.Exists(Path.Combine(outputDir, "Directory.Packages.props")))
        // The generated solution is written in both phases.
        Assert.True(File.Exists(Path.Combine(outputDir, "Fixture.sln")))
        // Sources, content and .editorconfig are not.
        Assert.False(File.Exists(Path.Combine(outputDir, "A", "Program.fs")))
        Assert.False(File.Exists(Path.Combine(outputDir, "A", "appsettings.json")))
        Assert.False(File.Exists(Path.Combine(outputDir, ".editorconfig"))))

// Both phases must share one mirror root, or the two outputs will not overlay.
[<Fact>]
let ``restore and full phases place project files at identical relative paths`` () =
    withTempDir (fun root ->
        writeProject (Path.Combine(root, "A")) "A" [] []
        writeSolution (Path.Combine(root, "Fixture.sln")) [ "A", "A/A.fsproj" ]

        let restoreDir = Path.Combine(root, "restore-output")
        let fullDir = Path.Combine(root, "full-output")

        let baseOptions =
            { ProjectPaths = [ Path.Combine(root, "A", "A.fsproj") ]
              OutputDir = Some restoreDir
              SolutionPath = None
              RestoreOnly = true
              Clean = false }

        Pipeline.isolate baseOptions |> ignore
        Pipeline.isolate { baseOptions with OutputDir = Some fullDir; RestoreOnly = false } |> ignore

        Assert.True(File.Exists(Path.Combine(restoreDir, "A", "A.fsproj")))
        Assert.True(File.Exists(Path.Combine(fullDir, "A", "A.fsproj"))))
```

- [x] **Step 2: Run the tests to verify they fail**

Run: `cd src/DotnetIsolate && dotnet test DotnetIsolate.IntegrationTests/DotnetIsolate.IntegrationTests.fsproj --filter "FullyQualifiedName~PipelineTests"`
Expected: FAIL — `RestoreOnly = true` still emits every file, so `Program.fs` exists in the output.

- [x] **Step 3: Write the implementation**

In `Pipeline.fs`, immediately **after** the mirror root is computed and **before** the link-strategy probe, narrow the file set:

```fsharp
    // The restore phase is applied after the mirror root is computed from the full set, so both
    // phases share one root and their outputs overlay exactly - which is what lets a Dockerfile
    // COPY the restore half, run `dotnet restore`, then COPY the full half over it.
    let filesToPlace =
        if options.RestoreOnly then
            Phase.restoreSubset projects allFiles
        else
            allFiles
```

Use `filesToPlace` for the probe (`LinkStrategy.probe (List.head filesToPlace) outputDir`), for `Materialize.materialize`, and for `FileCount`. Leave the solution-file generation in step 7 unchanged so it runs in both phases.

Update the default output directory to distinguish the phases:

```fsharp
        | None, [ single ] ->
            let name = Path.GetFileNameWithoutExtension(single)
            Path.GetFullPath(if options.RestoreOnly then $"{name}.restore" else name)
```

- [x] **Step 4: Add the CLI flag**

In `Program.fs`, add to `Arguments` and `Usage`:

```fsharp
    | Restore
```

```fsharp
            | Restore _ -> "emit only the files `dotnet restore` needs, for a cacheable Docker restore layer"
```

and set `RestoreOnly = results.Contains(Restore)`.

- [x] **Step 5: Run the tests to verify they pass**

Run: `cd src/DotnetIsolate && dotnet build DotnetIsolate.sln && dotnet test DotnetIsolate.UnitTests/DotnetIsolate.UnitTests.fsproj && dotnet test DotnetIsolate.IntegrationTests/DotnetIsolate.IntegrationTests.fsproj`
Expected: PASS.

- [x] **Step 6: Commit**

```bash
git add -A src/DotnetIsolate
git commit -m "feat: add --restore to emit only the dotnet restore inputs"
```

---

### Task 9: E2E — buildx-only harness and the inside-the-closure cache test

This closes the gap the spec identifies: the current suite only changes a file *outside* the isolated closure, which proves nothing about the restore phase.

**Files:**
- Create: `src/DotnetIsolate/DotnetIsolate.E2ETests/Fixtures/TwoPhase/Dockerfile`
- Modify: `src/DotnetIsolate/DotnetIsolate.E2ETests/DockerHarness.fs`
- Modify: `src/DotnetIsolate/DotnetIsolate.E2ETests/DockerCacheTests.fs`
- Modify: `src/DotnetIsolate/DotnetIsolate.E2ETests/DotnetIsolate.E2ETests.fsproj`

**Interfaces:**
- Consumes: `runDockerBuild`, `packLocalTool`, `copyFixtureToTempDir`, `touchUnrelatedFile`, `expensiveLayersCached`, `stepNotCached`.
- Produces: `DockerHarness.stepCached : output:string -> stepNeedle:string -> bool` (public wrapper over the existing private `isStepCached`, buildx only).

- [x] **Step 1: Create the Dockerfile fixture**

Create `src/DotnetIsolate/DotnetIsolate.E2ETests/Fixtures/TwoPhase/Dockerfile`:

```dockerfile
# syntax=docker/dockerfile:1
# The pattern the README documents: buildx only, the source reaching the isolate stage through a
# read-only bind mount rather than COPY (which would pull the whole context into the stage's
# layers), and the output split so the restore layer survives ordinary source edits.
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS isolate
COPY nupkg /nupkg
# --prerelease: NBGV versions non-tag builds as prerelease, which `dotnet tool install` otherwise
# silently filters out.
RUN dotnet tool install -g dotnet-isolate --add-source /nupkg --prerelease
ENV PATH="$PATH:/root/.dotnet/tools"
RUN --mount=type=bind,target=/src,source=. \
    dotnet isolate /src/ServiceA/ServiceA.csproj --restore -o /isolated/restore
RUN --mount=type=bind,target=/src,source=. \
    dotnet isolate /src/ServiceA/ServiceA.csproj -o /isolated/full

FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src
COPY --from=isolate /isolated/restore /src
RUN dotnet restore
COPY --from=isolate /isolated/full /src
RUN dotnet build -c Release --no-restore
RUN dotnet publish ServiceA/ServiceA.csproj -c Release --no-build -o /app

FROM mcr.microsoft.com/dotnet/runtime:8.0 AS run
COPY --from=build /app /app
ENTRYPOINT ["dotnet", "/app/ServiceA.dll"]
```

Note: the `nupkg` directory is in the build context, so the bind mount at `/src` also contains it. That is harmless — the tool only reads the project it is pointed at.

No project change is needed: `DotnetIsolate.E2ETests.fsproj:18` already copies fixtures by glob (`<None Include="Fixtures/**/*" CopyToOutputDirectory="PreserveNewest"/>`), so the new Dockerfile lands in `bin/Release/net8.0/Fixtures/TwoPhase/` automatically.

- [x] **Step 2: Write the failing test**

In `DockerCacheTests.fs`, add the fixture path and the new test:

```fsharp
let private twoPhaseDockerfile =
    Path.Combine(AppContext.BaseDirectory, "Fixtures", "TwoPhase", "Dockerfile")
```

```fsharp
/// The property the whole --restore feature rests on: a change to a file *inside* the isolated
/// closure must still leave `dotnet restore` cache-hit, because the restore half of the output
/// contains no sources and is therefore byte-identical. The other tests here change a file
/// outside the closure, which leaves the entire output unchanged and so proves nothing about this.
[<Fact>]
let ``restore layer stays cached when a file inside the closure changes`` () =
    let contextDir = copyFixtureToTempDir fixtureSourceDir
    let tag = $"dotnet-isolate-e2e-twophase-{Guid.NewGuid():N}"

    try
        packLocalTool toolProjectPath (Path.Combine(contextDir, "nupkg"))

        let exitCode1, output1 = runDockerBuild true contextDir twoPhaseDockerfile tag
        Assert.True((exitCode1 = 0), $"first build failed:\n{output1}")

        // A source file *inside* ServiceA's closure - not an unrelated one.
        touchUnrelatedFile (Path.Combine(contextDir, "ServiceA", "Program.cs"))

        let exitCode2, output2 = runDockerBuild true contextDir twoPhaseDockerfile tag
        Assert.True((exitCode2 = 0), $"second build failed:\n{output2}")

        // Sanity: the change really did reach the build, so the assertion below isn't vacuous.
        Assert.True(
            stepNotCached true output2 "dotnet build -c Release",
            $"expected the build layer to rerun after a source change inside the closure:\n{output2}"
        )

        // The actual assertion: restore survived, because the restore half is unchanged.
        Assert.True(
            stepCached output2 "dotnet restore",
            $"expected the restore layer to stay cached after a source change inside the closure:\n{output2}"
        )
    finally
        removeImage tag
        deleteIfExists contextDir
```

- [x] **Step 3: Run the test to verify it fails**

Run: `cd src/DotnetIsolate && dotnet test DotnetIsolate.E2ETests/DotnetIsolate.E2ETests.fsproj --filter "FullyQualifiedName~restore layer stays cached"`
Expected: compile error — `stepCached` is not defined.

- [x] **Step 4: Expose `stepCached` and drop the classic builder**

In `DockerHarness.fs`, add next to `stepNotCached`:

```fsharp
/// True if the given step cache-hit. BuildKit only - the classic builder is no longer supported.
let stepCached (output: string) (stepNeedle: string) = isStepCached true output stepNeedle
```

In `DockerCacheTests.fs`, convert the two existing `[<Theory>]`/`[<InlineData(true)>]`/`[<InlineData(false)>]` tests into `[<Fact>]`s that pass `true` for `useBuildKit`, per the spec's DI-1 change. Leave `runDockerBuild`'s `useBuildKit` parameter in place — the harness still takes it — but no test passes `false`.

- [x] **Step 5: Run the E2E suite**

Run: `cd src/DotnetIsolate && dotnet test DotnetIsolate.E2ETests/DotnetIsolate.E2ETests.fsproj`
Expected: PASS. This needs a Docker daemon and pulls SDK images, so it is slow on a cold cache.

If the new test fails because `dotnet restore` reruns, do **not** relax the assertion — diff the two `/isolated/restore` trees to find which file differs, and fix `Phase.restoreSubset` so that file is correctly included or excluded. The verified premise is that `COPY --from` is content-keyed, so an unchanged restore half must cache-hit.

- [x] **Step 6: Commit**

```bash
git add -A src/DotnetIsolate/DotnetIsolate.E2ETests
git commit -m "test: prove the restore layer stays cached across in-closure source changes"
```

---

### Task 10: Rewrite the README and amend the authoritative documents

`AGENTS.md` treats `REQUIREMENTS.md` and `DESIGN.md` as the source of truth, so leaving them describing the old behaviour would make them lie.

**Files:**
- Modify: `README.md`
- Modify: `REQUIREMENTS.md:34` (FR-7), `:84` (DI-1), `:141` (QP-12)
- Modify: `DESIGN.md` (the "Docker integration patterns" section, and the pipeline steps 6-7 description)

- [x] **Step 1: Rewrite the README**

Replace the whole "How will this help me keep my docker image small?" section (lines 40-91) with a single documented pattern. Keep "Usage" and "What about files outside the solution folder?", tightening the latter. Cut the README substantially overall.

The new section must explain the *mechanism*, not just show a recipe:

- `COPY --from=<stage>` is keyed on the checksum of the copied bytes, not on the producing stage's layer history. The isolate stage may rerun on every build while everything below it still cache-hits.
- Content-keyed means mtime-insensitive, which matters because the tool hardlinks (so output files inherit source mtimes) and a fresh CI clone stamps every file with a new mtime.
- `--restore` splits the output at the point where the cache should hold: the restore half changes only when a project file or a build-props file changes, so `RUN dotnet restore` survives ordinary source edits.
- The bind mount is read-only, which is all the tool needs; it keeps the whole context out of the isolate stage's layers, unlike `COPY . /src`.
- Hardlinking across the bind mount fails, and `LinkStrategy.probe` detects that once upfront and falls back to copying. No configuration required.
- Paths inside the isolated tree are relative to the computed mirror root, which is not always the solution root. Run the tool once and look at the output before writing `COPY` and `publish` paths.

Use the Dockerfile from the spec (`docs/superpowers/specs/2026-08-10-isolate-phases-and-safety-design.md`, section E) verbatim, with `--mount=type=bind,target=/src`.

Also document the new flags in "Usage":

```
dotnet isolate path/to/<Project>.csproj [more.csproj ...] [-o <dir>] [-s <path>] [--restore] [--clean]
```

- [x] **Step 2: Amend REQUIREMENTS.md**

- **FR-7**: rewrite from "If the output folder already exists, the tool deletes it and recreates it from scratch" to: the tool merges into an existing output folder and reports entries it did not produce; `--clean` restores delete-and-recreate. Add that the tool refuses an output directory that would consume its own inputs, and that resolved inputs under the output directory are excluded.
- **DI-1**: rewrite from "Two supported Dockerfile patterns are documented, both of which work under any builder" to one documented pattern, buildx only, using a read-only bind mount and the two-phase `--restore` split.
- **QP-12**: extend to cover a change *inside* the isolated closure asserting the restore layer stays cached; drop the classic-builder half of the matrix.
- Add new requirements for `--restore`, `--clean`, multiple entry projects, and warn-on-missing-file. Follow the existing `FR-n` numbering convention and continue from the highest number in use.

- [x] **Step 3: Amend DESIGN.md**

Update the "Docker integration patterns" section to the single buildx pattern and record the verified finding that `COPY --from` is content-keyed and mtime-insensitive. Update pipeline step 6 (materialization no longer deletes; the output directory is partitioned out of the input set before the mirror root is computed) and note that step 7's solution generation runs in both phases. Add `OutputSafety.fs` and `Phase.fs` to any module listing.

- [x] **Step 4: Verify the documented Dockerfile actually works**

The README's Dockerfile must not be aspirational. Confirm it matches the E2E fixture from Task 9 (which is executed by a real `docker build`) apart from the project path and the `dotnet tool install` source.

Run: `diff <(sed 's|ServiceA|Service1|g' src/DotnetIsolate/DotnetIsolate.E2ETests/Fixtures/TwoPhase/Dockerfile) -` against the README block, or compare them by eye — the `COPY --from` / `RUN` sequence must be identical.

- [x] **Step 5: Check line endings did not regress**

Run: `git diff --stat`
Expected: the changed-line counts are proportionate to the edits. A one-line edit reporting hundreds of changed lines means the file was rewritten with different line endings — `.gitattributes` enforces LF.

- [x] **Step 6: Commit**

```bash
git add README.md REQUIREMENTS.md DESIGN.md
git commit -m "docs: document the buildx two-phase pattern and amend FR-7, DI-1, QP-12"
```

---

### Task 11: Full verification

- [x] **Step 1: Build and run every suite**

```bash
cd src/DotnetIsolate
dotnet build DotnetIsolate.sln
dotnet test DotnetIsolate.UnitTests/DotnetIsolate.UnitTests.fsproj
dotnet test DotnetIsolate.IntegrationTests/DotnetIsolate.IntegrationTests.fsproj
dotnet test DotnetIsolate.E2ETests/DotnetIsolate.E2ETests.fsproj
```

Expected: all green. Report the actual counts; do not claim success without the output.

- [x] **Step 2: Re-run the original reproductions by hand**

```bash
cd src/DotnetIsolate && dotnet build DotnetIsolate/DotnetIsolate.fsproj -c Release
W=$(mktemp -d) && cp -r src/TestSolutions/DiamondWithIncludedFilesSln $W/repo
CLI=$PWD/DotnetIsolate/DotnetIsolate/bin/Release/net8.0/DotnetIsolate
# Case C: must fail with a clear error, and the source tree must survive.
(cd $W/repo && $CLI ServiceA/ServiceA.csproj -o $W/repo); echo "exit=$?"
find $W/repo -name '*.csproj' | sort
# Case B: must succeed twice.
(cd $W/repo && $CLI ServiceA/ServiceA.csproj -o $W/repo/ServiceA/out) && \
(cd $W/repo && $CLI ServiceA/ServiceA.csproj -o $W/repo/ServiceA/out); echo "exit=$?"
```

Expected: case C exits 1 with a message naming the output directory, and all five `.csproj` files are still present. Case B exits 0 both times.

- [x] **Step 3: Note anything unverifiable locally**

Per `AGENTS.md`, this work touches path construction and comparison (`OutputSafety`, `Phase`), so state explicitly in the final summary what could not be verified on the local platform — Windows path behaviour in particular is only exercised by CI.

- [x] **Step 4: Push the branch**

```bash
git push -u origin feat/isolate-phases-and-safety
```

## Self-Review

**Spec coverage:** Section A → Tasks 1-3. Section B → Tasks 7-8. Section C → Task 4. Section D → Tasks 5-6. Section E → Task 10. "Requirements to amend" → Task 10. "Testing" → distributed across tasks, with E2E in Task 9 and the reproductions in Task 11. Out-of-scope items are not implemented anywhere.

**Type consistency:** `Materialize.materialize` takes `(strategy, clean, mirrorRoot, outputRoot, files)` and returns `string list` in Tasks 2, 3 and 8. `FileResolution.ResolvedFiles` has `Files`/`MissingItemFiles` in Task 4 and is consumed under those names in Task 4 Step 4. `OutputSafety.InputPartition` has `Kept`/`ExcludedUnderOutput` in Tasks 1 and 3. `IsolateOptions` has all five fields from Task 3 onward, and every later task constructs it with all five. `IsolateResult` gains `ExcludedUnderOutput`/`StaleEntries` in Task 3 and `MissingFiles` in Task 4.

**Known ordering constraint:** Task 3 introduces `ProjectPaths` and `RestoreOnly` into `IsolateOptions` before Tasks 6 and 8 give them behaviour. This is called out in Task 3 Step 1 and is deliberate — it avoids two churning rewrites of every call site.
