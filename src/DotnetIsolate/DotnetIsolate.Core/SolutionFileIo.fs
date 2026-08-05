/// Real filesystem-writing counterpart to SolutionFile.fs's pure filterSln logic, split out for
/// the same reason as FileResolutionIo.fs: coverlet's coverage-exclude filter works at whole-class
/// granularity, so this file lets coverage.unit.runsettings exclude the IO half by class name
/// while the integration coverage run still measures it.
module DotnetIsolate.Core.SolutionFileIo

open System.IO
open System.Text

/// Writes `content` (as produced by SolutionFile.filterSln) to `path` with a UTF-8 BOM, matching
/// what `dotnet sln add` itself produces.
let write (path: string) (content: string) =
    File.WriteAllText(path, content, UTF8Encoding(true))
