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

/// Fails when the output directory is unsafe to write into, given the partition and whether
/// `--clean` (delete-and-recreate) was requested.
///
/// Two rules, because the two output modes tolerate different amounts of overlap:
///
/// 1. Always: the output directory must not consume its *own* inputs - it must not equal, or be an
///    ancestor of, every resolved file, leaving nothing to materialize. This is the guard against
///    the destructive case that motivated the whole check: pointing `-o` at the solution root
///    previously deleted the entire source tree (FR-7's delete-and-recreate) and only then failed.
///
/// 2. With `--clean` only: the output directory must contain *no* resolved input at all. Rule 1
///    alone is not enough for a run that deletes, because it only fires on *total* overlap.
///    `--clean -o src/Shared`, where `src/Shared` holds real sources but other inputs live
///    elsewhere, leaves a non-empty kept set and so passed - and then Directory.Delete removed
///    `src/Shared`. The default merge mode can live with partial overlap (its worst case is an
///    incomplete output tree plus a warning about the dropped inputs, nothing is destroyed), but
///    `--clean` deletes, so it has to demand the output be disjoint from the inputs entirely.
///
/// Validation runs before any filesystem mutation.
let validate (clean: bool) (outputDir: string) (partition: InputPartition) : Result<unit, string> =
    if List.isEmpty partition.Kept then
        Error
            $"the output directory {outputDir} contains every resolved input file, so it would consume its own inputs; choose an output directory outside the source tree"
    elif clean && not (List.isEmpty partition.ExcludedUnderOutput) then
        // Naming a concrete casualty makes the refusal actionable: "it contains inputs" invites
        // the user to assume stale output, which --clean is exactly the flag for deleting.
        let example = List.head partition.ExcludedUnderOutput
        let others = List.length partition.ExcludedUnderOutput - 1

        let alsoOthers =
            if others > 0 then
                $" and {others} other resolved input file(s)"
            else
                ""

        Error
            $"--clean would delete the output directory {outputDir}, but it contains the resolved input file {example}{alsoOthers}; use an output directory that contains none of the inputs, or drop --clean to merge instead"
    else
        Ok()
