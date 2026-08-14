/// Real filesystem implementation of BuildArtifacts.ContainsProjectFile, kept in its own file so
/// coverlet's whole-class exclude filter can scope it out of the unit coverage gate (QP-5)
/// without affecting the integration gate (QP-4) - see FileResolutionIo.fs for the full rationale.
module DotnetIsolate.Core.BuildArtifactsIo

open System.Collections.Concurrent
open System.IO

let private projectFilePatterns = [ "*.csproj"; "*.fsproj"; "*.vbproj" ]

/// A `BuildArtifacts.ContainsProjectFile` backed by the real filesystem.
///
/// Memoized: a large solution asks about the same `obj/` parent once per resolved file beneath it,
/// which is hundreds of identical directory enumerations otherwise. A ConcurrentDictionary because
/// the pipeline evaluates projects in parallel (see Pipeline.fs). The cache is keyed by absolute
/// directory path and this is a single shared value rather than one instance per run, which is
/// safe only because the answer cannot change under us mid-run: the tool never creates or deletes
/// a project file, it only reads the source tree and writes into its own output directory.
let containsProjectFile: BuildArtifacts.ContainsProjectFile =
    let cache = ConcurrentDictionary<string, bool>()

    fun directory ->
        cache.GetOrAdd(
            directory,
            fun dir ->
                Directory.Exists(dir)
                && projectFilePatterns
                   |> List.exists (fun pattern -> Directory.EnumerateFiles(dir, pattern) |> Seq.isEmpty |> not)
        )
