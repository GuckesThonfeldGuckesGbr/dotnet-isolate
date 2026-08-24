module DotnetIsolate.Core.Report

/// The FR-14/15/16 diagnostic lines shared by both verbs: artifact-exclusion count, foreign-
/// project-file groups, missing item files, and entries outside the discovered solution.
/// Formatting lives here rather than in Program.fs so it is unit-testable: the CLI has no test
/// project, and these strings are the tool's whole report of what it silently dropped or flagged.
let private commonWarnings
    (excludedArtifacts: string list)
    (foreignProjectFiles: ForeignFiles.ForeignGroup list)
    (missingFiles: string list)
    (entriesOutsideDiscoveredSolution: string list)
    (solutionRoot: SolutionDiscovery.SolutionRoot option)
    : string list =
    [ if not (List.isEmpty excludedArtifacts) then
          $"note: skipped {excludedArtifacts.Length} generated build artifact(s) (bin/, obj/, node_modules/, build and coverage logs)"

      for group in foreignProjectFiles do
          $"note: {group.Files.Length} file(s) resolved from {group.Directory}, whose project is not in the isolated closure"

      for entry in missingFiles do
          $"warning: {entry} is referenced by a project but does not exist; skipped"

      // solutionRoot is always Some here when this list is non-empty (see Pipeline.fs).
      for entry in entriesOutsideDiscoveredSolution do
          $"warning: {entry} lies outside the discovered solution ({solutionRoot.Value.SolutionFile}); the generated solution file may not include it" ]

/// The warning lines an `isolate` run should print to stderr, in order.
///
/// Two of the categories are summarised rather than listed. Artifact exclusions (FR-15) and stale
/// output entries (FR-7) arrive in bulk - hundreds of lines carrying one bit of information
/// between them - while a missing item file (FR-14) and a resolved input sitting under the output
/// directory are each rare and individually actionable, so those stay per file.
let warnings (result: Pipeline.IsolateResult) : string list =
    commonWarnings
        result.ExcludedArtifacts
        result.ForeignProjectFiles
        result.MissingFiles
        result.EntriesOutsideDiscoveredSolution
        result.SolutionRoot
    @ [ if not (List.isEmpty result.StaleEntries) then
            $"warning: the output directory was not clean ({result.StaleEntries.Length} pre-existing file(s) left in place); use --clean to replace it"

        for entry in result.ExcludedUnderOutput do
            $"warning: ignoring {entry}, which lives under the output directory" ]

/// The warning lines a `list-files` run should print to stderr (FR-17). Only the shared FR-14/15/
/// 16 categories apply - StaleEntries and ExcludedUnderOutput are materialization-only concerns
/// list-files never has, since it never writes an output directory.
let listFilesWarnings (result: Pipeline.ListFilesResult) : string list =
    commonWarnings
        result.ExcludedArtifacts
        result.ForeignProjectFiles
        result.MissingFiles
        result.EntriesOutsideDiscoveredSolution
        result.SolutionRoot
