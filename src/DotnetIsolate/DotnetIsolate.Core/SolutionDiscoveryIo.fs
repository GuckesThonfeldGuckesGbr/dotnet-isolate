/// Real filesystem implementation of SolutionDiscovery.SolutionFilesInDirectory, kept in its own
/// file so coverlet's whole-class exclude filter can scope it out of the unit coverage gate
/// (QP-5) without affecting the integration gate (QP-4) - see FileResolutionIo.fs for the full
/// rationale.
module DotnetIsolate.Core.SolutionDiscoveryIo

open System.IO

/// A `SolutionDiscovery.SolutionFilesInDirectory` backed by the real filesystem.
let solutionFilesOnDisk: SolutionDiscovery.SolutionFilesInDirectory =
    fun directory ->
        if Directory.Exists(directory) then
            (Directory.GetFiles(directory, "*.sln") |> Array.toList)
            @ (Directory.GetFiles(directory, "*.slnx") |> Array.toList)
        else
            []
