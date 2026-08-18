module DotnetIsolate.Core.Report

/// The warning lines a run should print to stderr, in order.
///
/// Two of the categories are summarised rather than listed. Artifact exclusions (FR-15) and stale
/// output entries (FR-7) arrive in bulk - hundreds of lines carrying one bit of information
/// between them - while a missing item file (FR-14) and a resolved input sitting under the output
/// directory are each rare and individually actionable, so those stay per file.
///
/// Formatting lives here rather than in Program.fs so it is unit-testable: the CLI has no test
/// project, and these strings are the tool's whole report of what it silently dropped.
let warnings (result: Pipeline.IsolateResult) : string list =
    [ if not (List.isEmpty result.ExcludedArtifacts) then
          $"note: skipped {result.ExcludedArtifacts.Length} generated build artifact(s) (bin/, obj/, node_modules/, build and coverage logs)"

      for group in result.ForeignProjectFiles do
          $"note: {group.Files.Length} file(s) resolved from {group.Directory}, whose project is not in the isolated closure"

      if not (List.isEmpty result.StaleEntries) then
          $"warning: the output directory was not clean ({result.StaleEntries.Length} pre-existing file(s) left in place); use --clean to replace it"

      for entry in result.ExcludedUnderOutput do
          $"warning: ignoring {entry}, which lives under the output directory"

      for entry in result.MissingFiles do
          $"warning: {entry} is referenced by a project but does not exist; skipped"

      // result.SolutionRoot is always Some here when this list is non-empty (see Pipeline.fs).
      for entry in result.EntriesOutsideDiscoveredSolution do
          $"warning: {entry} lies outside the discovered solution ({result.SolutionRoot.Value.SolutionFile}); the generated solution file may not include it" ]
