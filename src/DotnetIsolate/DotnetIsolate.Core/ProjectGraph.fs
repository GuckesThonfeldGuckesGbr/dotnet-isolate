module DotnetIsolate.Core.ProjectGraph

/// Resolves the absolute paths of a project's direct ProjectReference targets.
type ProjectReferenceResolver = string -> string list

/// Resolves the transitive closure of project references starting from `entryProject`,
/// deduplicated by resolved absolute path - so a diamond dependency reachable via more
/// than one path is only visited (and returned) once.
let resolve (getReferences: ProjectReferenceResolver) (entryProject: string) : string list =
    let rec go (visited: Set<string>) (toVisit: string list) (found: string list) =
        match toVisit with
        | [] -> List.rev found
        | current :: rest ->
            if Set.contains current visited then
                go visited rest found
            else
                let visited = Set.add current visited
                let references = getReferences current
                go visited (references @ rest) (current :: found)

    go Set.empty [ entryProject ] []
