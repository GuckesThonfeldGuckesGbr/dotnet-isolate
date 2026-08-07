/// Real filesystem implementation of ImplicitFiles.FilesInDirectory, kept in its own file so
/// coverlet's whole-class exclude filter can scope it out of the unit coverage gate (QP-5)
/// without affecting the integration gate (QP-4) - see FileResolutionIo.fs for the full
/// rationale.
module DotnetIsolate.Core.ImplicitFilesIo

open System.IO

/// The well-known repo-level files MSBuild implicitly consumes from parent directories,
/// even though a project never references them explicitly (FR-5).
///
/// `.editorconfig` is here as well as in FileResolution's `ancestorGlobbedItemTypes`, and the
/// overlap is deliberate: the C# SDK exposes its own `.editorconfig` discovery as an
/// `EditorConfigFiles` item, which finds nested ones inside a project that this walk-up never
/// looks at - but the F# SDK leaves that item empty at evaluation time (verified directly), so
/// F# projects would otherwise lose their analyzer configuration entirely. This walk-up is
/// language-agnostic and covers them; duplicates between the two are deduplicated downstream.
let wellKnownFileNames =
    [ "Directory.Build.props"
      "Directory.Build.targets"
      "Directory.Packages.props"
      "NuGet.config"
      "global.json"
      ".editorconfig" ]

/// An `ImplicitFiles.FilesInDirectory` backed by the real filesystem.
let filesOnDisk: ImplicitFiles.FilesInDirectory =
    fun directory ->
        wellKnownFileNames
        |> List.map (fun name -> Path.Combine(directory, name))
        |> List.filter File.Exists
