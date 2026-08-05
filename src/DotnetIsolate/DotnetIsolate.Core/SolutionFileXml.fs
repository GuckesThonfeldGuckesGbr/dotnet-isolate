module DotnetIsolate.Core.SolutionFileXml

open System.IO
open System.Text
open System.Xml.Linq

let private projectElementName = "Project"
let private folderElementName = "Folder"

let private isProject (el: XElement) = el.Name.LocalName = projectElementName
let private isFolder (el: XElement) = el.Name.LocalName = folderElementName

/// Recursively drops any `<Folder>` left with no remaining `<Project>`/`<Folder>` children, after
/// non-included `<Project>` elements have already been removed - mirrors SolutionFile.filterSln's
/// unconditional dropping of .sln solution folders: folders are never reconstructed, only kept
/// while they still have real content under them.
let rec private pruneEmptyFolders (el: XElement) =
    el.Elements() |> Seq.toList |> List.iter pruneEmptyFolders

    if isFolder el && not (el.Elements() |> Seq.exists (fun c -> isProject c || isFolder c)) then
        el.Remove()

/// Generates a .slnx containing only the `<Project>` entries whose resolved absolute `Path` is in
/// `includedAbsolutePaths`, using `sourceContent` as a template (FR-3, pipeline step 7) - the XML
/// counterpart to SolutionFile.filterSln. `solutionDir` is the directory the source .slnx lives
/// in, needed to resolve each entry's (relative, possibly backslash-separated) declared path to
/// an absolute one for comparison.
let filterSlnx (solutionDir: string) (includedAbsolutePaths: Set<string>) (sourceContent: string) : string =
    let doc = XDocument.Parse(sourceContent)

    let resolvedPath (el: XElement) =
        let declaredPath = el.Attribute(XName.Get "Path").Value
        Path.GetFullPath(Path.Combine(solutionDir, declaredPath.Replace('\\', Path.DirectorySeparatorChar)))

    match doc.Root with
    | null -> ()
    | root ->
        root.Descendants(XName.Get projectElementName)
        |> Seq.toList
        |> List.filter (fun el -> not (includedAbsolutePaths.Contains(resolvedPath el)))
        |> List.iter (fun el -> el.Remove())

        pruneEmptyFolders root

    doc.ToString()

/// Writes `content` (as produced by filterSlnx) to `path`. Unlike .sln, real .slnx files carry no
/// UTF-8 BOM (verified directly against a real .slnx fixture), so none is added here.
let write (path: string) (content: string) =
    File.WriteAllText(path, content, UTF8Encoding(false))
