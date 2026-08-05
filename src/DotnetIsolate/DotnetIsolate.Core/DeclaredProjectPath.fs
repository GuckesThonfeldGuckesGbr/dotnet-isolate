module DotnetIsolate.Core.DeclaredProjectPath

open System.IO

/// Resolves a solution's declared (relative, often backslash-separated even on Unix) project path
/// against the directory the solution file lives in, to an absolute path comparable against
/// resolved project paths elsewhere in the pipeline. Shared by SolutionFile.filterSln and
/// SolutionFileXml.filterSlnx, which both declare project paths the same way.
let resolve (solutionDir: string) (declaredPath: string) : string =
    Path.GetFullPath(Path.Combine(solutionDir, declaredPath.Replace('\\', Path.DirectorySeparatorChar)))
