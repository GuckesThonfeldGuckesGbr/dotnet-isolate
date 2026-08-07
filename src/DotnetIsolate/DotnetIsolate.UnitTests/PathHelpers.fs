module DotnetIsolate.UnitTests.PathHelpers

open System
open System.IO

/// The filesystem root in the platform's own *fully qualified* form: "/" on Unix, "C:\" (or
/// whatever drive the tests run from) on Windows.
///
/// The drive letter is the point. "\repo\src" is a perfectly valid *rooted* Windows path, but it
/// is not a *fully qualified* one, and APIs that demand full qualification reject it - found the
/// hard way on Windows CI, where Path.GetFullPath(value, basePath) threw "Basepath argument is not
/// fully qualified" for every property-derived path test. Unix has no such distinction, so the
/// difference is invisible when developing on Linux or macOS.
let private root = Path.GetPathRoot(Directory.GetCurrentDirectory())

/// Builds a platform-native, fully qualified path from `segments`, e.g. `path [ "repo"; "src" ]`
/// gives "/repo/src" on Unix and "C:\repo\src" on Windows.
///
/// Always use this instead of a hardcoded "/repo/src" literal, which only round-trips correctly
/// through Path.GetDirectoryName on Unix (verified directly: Windows CI failed walking such
/// literals up, since a bare forward slash isn't Windows' native root form).
let path (segments: string list) : string =
    Path.Combine(root, String.Join(string Path.DirectorySeparatorChar, segments))
