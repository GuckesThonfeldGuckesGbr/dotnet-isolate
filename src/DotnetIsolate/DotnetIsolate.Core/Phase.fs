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
/// without it. In SDK-style C# projects it already reaches the resolved set through the SDK's
/// default `None` glob, which collects every otherwise-unmatched file in a project directory - but
/// F# projects disable those default item globs (`EnableDefaultCompileItems` and
/// `EnableDefaultNoneItems` are both `false`), so its presence in the resolved set is not
/// guaranteed there. Matching by name, as this module does, is correct either way.
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
