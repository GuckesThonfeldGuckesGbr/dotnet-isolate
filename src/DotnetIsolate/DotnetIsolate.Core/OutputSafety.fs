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
