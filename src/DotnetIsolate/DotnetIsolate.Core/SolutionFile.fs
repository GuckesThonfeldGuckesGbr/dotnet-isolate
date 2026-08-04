module DotnetIsolate.Core.SolutionFile

open System.IO
open System.Text
open System.Text.RegularExpressions

/// One `Project(...)` ... `EndProject` block from a classic .sln file.
type private ProjectEntry =
    { Guid: string
      DeclaredPath: string
      Lines: string list }

type private Parsed =
    { HeaderLines: string list
      Entries: ProjectEntry list
      GlobalLines: string list
      TrailerLines: string list }

let private projectHeaderRegex =
    Regex(
        @"^Project\(""\{[0-9A-Fa-f-]+\}""\)\s*=\s*""[^""]+"",\s*""(?<path>[^""]+)"",\s*""\{(?<guid>[0-9A-Fa-f-]+)\}""\s*$"
    )

let private isProjectHeader (line: string) = projectHeaderRegex.IsMatch(line)
let private isEndProject (line: string) = line.Trim() = "EndProject"

let private splitLines (content: string) : string list =
    content.Replace("\r\n", "\n").Split('\n') |> Array.toList

/// Splits a .sln's lines into: the header (format/version comments before the first project),
/// each Project(...)...EndProject block, the Global...EndGlobal block, and anything after it.
/// Scoped to the structure `dotnet sln add` actually produces (verified directly) - hand-edited
/// or unusually-laid-out .sln files are not exhaustively handled.
let private parse (lines: string list) : Parsed =
    let rec collectHeader (ls: string list) (acc: string list) =
        match ls with
        | l :: rest when not (isProjectHeader l) && l.Trim() <> "Global" -> collectHeader rest (l :: acc)
        | _ -> (List.rev acc), ls

    let rec collectEntries (ls: string list) (acc: ProjectEntry list) =
        match ls with
        | l :: rest when isProjectHeader l ->
            let m = projectHeaderRegex.Match(l)
            let endIdx = rest |> List.findIndex isEndProject
            let block = l :: (rest |> List.take (endIdx + 1))
            let remaining = rest |> List.skip (endIdx + 1)

            let entry =
                { Guid = m.Groups["guid"].Value
                  DeclaredPath = m.Groups["path"].Value
                  Lines = block }

            collectEntries remaining (entry :: acc)
        | _ -> (List.rev acc), ls

    let rec collectGlobal (ls: string list) (acc: string list) =
        match ls with
        | [] -> (List.rev acc), []
        | l :: rest ->
            let acc = l :: acc
            if l.Trim() = "EndGlobal" then (List.rev acc), rest else collectGlobal rest acc

    let headerLines, afterHeader = collectHeader lines []
    let entries, afterEntries = collectEntries afterHeader []
    let globalLines, trailerLines = collectGlobal afterEntries []

    { HeaderLines = headerLines
      Entries = entries
      GlobalLines = globalLines
      TrailerLines = trailerLines }

let private sectionNameRegex = Regex(@"^\s*GlobalSection\(([^)]+)\)")
let private guidRegex = Regex(@"\{([0-9A-Fa-f-]+)\}")

/// Filters GlobalSection content by kept project GUIDs: ProjectConfigurationPlatforms lines are
/// kept only for a kept GUID; NestedProjects (solution-folder hierarchy) is dropped entirely,
/// since solution folders are never kept (see filterSln) and nesting info about them is
/// meaningless without the folders themselves. Every other section is passed through verbatim.
let private filterGlobalLines (keptGuids: Set<string>) (globalLines: string list) : string list =
    let rec go (ls: string list) (currentSection: string option) (acc: string list) =
        match ls with
        | [] -> List.rev acc
        | l :: rest ->
            let trimmed = l.Trim()

            if trimmed.StartsWith("GlobalSection(") then
                let name = sectionNameRegex.Match(l).Groups[1].Value
                go rest (Some name) (l :: acc)
            elif trimmed = "EndGlobalSection" then
                go rest None (l :: acc)
            else
                match currentSection with
                | Some "ProjectConfigurationPlatforms" ->
                    let m = guidRegex.Match(l)
                    let keep = m.Success && keptGuids.Contains(m.Groups[1].Value)
                    go rest currentSection (if keep then l :: acc else acc)
                | Some "NestedProjects" -> go rest currentSection acc
                | _ -> go rest currentSection (l :: acc)

    go globalLines None []

/// Generates a .sln containing only the project entries whose resolved absolute path is in
/// `includedAbsolutePaths`, using `sourceContent` as a template (FR-3, pipeline step 7).
/// `solutionDir` is the directory the source .sln lives in, needed to resolve each entry's
/// (relative, often backslash-separated even on Unix) declared path to an absolute one for
/// comparison. Solution folders are dropped unconditionally - their declared "path" never
/// matches a real included project file, so the same filter that drops unrelated projects
/// drops them too, with no special-casing needed.
let filterSln (solutionDir: string) (includedAbsolutePaths: Set<string>) (sourceContent: string) : string =
    let parsed = parse (splitLines sourceContent)

    let resolvedPath (entry: ProjectEntry) =
        Path.GetFullPath(Path.Combine(solutionDir, entry.DeclaredPath.Replace('\\', Path.DirectorySeparatorChar)))

    let keptEntries =
        parsed.Entries
        |> List.filter (fun e -> includedAbsolutePaths.Contains(resolvedPath e))

    let keptGuids = keptEntries |> List.map (fun e -> e.Guid) |> Set.ofList
    let keptEntryLines = keptEntries |> List.collect (fun e -> e.Lines)
    let filteredGlobalLines = filterGlobalLines keptGuids parsed.GlobalLines

    let outputLines =
        List.concat [ parsed.HeaderLines; keptEntryLines; filteredGlobalLines; parsed.TrailerLines ]

    outputLines |> String.concat "\r\n"

/// Writes `content` (as produced by filterSln) to `path` with a UTF-8 BOM, matching what
/// `dotnet sln add` itself produces.
let write (path: string) (content: string) =
    File.WriteAllText(path, content, UTF8Encoding(true))
