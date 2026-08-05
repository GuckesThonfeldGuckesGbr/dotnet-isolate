/// Real filesystem implementation of ImplicitFiles.FilesInDirectory, kept in its own file so
/// coverlet's whole-class exclude filter can scope it out of the unit coverage gate (QP-5)
/// without affecting the integration gate (QP-4) - see FileResolutionIo.fs for the full
/// rationale.
module DotnetIsolate.Core.ImplicitFilesIo

open System.IO

/// The well-known repo-level files MSBuild implicitly consumes from parent directories,
/// even though a project never references them explicitly (FR-5).
let wellKnownFileNames =
    [ "Directory.Build.props"
      "Directory.Build.targets"
      "Directory.Packages.props"
      "NuGet.config"
      "global.json" ]

/// An `ImplicitFiles.FilesInDirectory` backed by the real filesystem.
let filesOnDisk: ImplicitFiles.FilesInDirectory =
    fun directory ->
        wellKnownFileNames
        |> List.map (fun name -> Path.Combine(directory, name))
        |> List.filter File.Exists
