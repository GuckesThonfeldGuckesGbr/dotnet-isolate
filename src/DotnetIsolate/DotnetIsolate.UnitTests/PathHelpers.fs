module DotnetIsolate.UnitTests.PathHelpers

open System
open System.IO

/// Builds a platform-native rooted path from `segments`, e.g. `path [ "repo"; "src"; "A" ]` gives
/// "/repo/src/A" on Unix or "\repo\src\A" on Windows - both are valid single-separator-rooted
/// paths, unlike a hardcoded "/repo/src/A" literal, which only round-trips correctly through
/// Path.GetDirectoryName on Unix (verified directly: Windows CI failed walking such literals up
/// with Path.GetDirectoryName, since a bare forward slash isn't Windows' native root form).
let path (segments: string list) : string =
    string Path.DirectorySeparatorChar + String.Join(string Path.DirectorySeparatorChar, segments)
