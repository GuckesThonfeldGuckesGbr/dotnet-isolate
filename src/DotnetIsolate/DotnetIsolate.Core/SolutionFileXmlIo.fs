/// Real filesystem-writing counterpart to SolutionFileXml.fs's pure filterSlnx logic, split out
/// for the same reason as FileResolutionIo.fs: coverlet's coverage-exclude filter works at
/// whole-class granularity, so this file lets coverage.unit.runsettings exclude the IO half by
/// class name while the integration coverage run still measures it.
module DotnetIsolate.Core.SolutionFileXmlIo

open System.IO
open System.Text

/// Writes `content` (as produced by SolutionFileXml.filterSlnx) to `path`. Unlike .sln, real
/// .slnx files carry no UTF-8 BOM (verified directly against a real .slnx fixture), so none is
/// added here.
let write (path: string) (content: string) =
    File.WriteAllText(path, content, UTF8Encoding(false))
