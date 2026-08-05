module DotnetIsolate.Core.ProjectGraph

/// Resolves the absolute paths of a project's direct ProjectReference targets.
type ProjectReferenceResolver = string -> string list

/// Resolves the transitive closure of project references starting from `entryProject`,
/// deduplicated by resolved absolute path - so a diamond dependency reachable via more than one
/// path is only visited (and returned) once. Evaluates each BFS level's projects concurrently
/// (`getReferences` typically shells out to a real MSBuild process per call - ~150-200ms of pure
/// process/host-startup overhead per call, independent of project size, verified directly) rather
/// than one at a time: most real solutions are wide (many sibling projects) but shallow (few
/// levels of references), so evaluating a whole level at once instead of walking it project by
/// project turns O(project count) sequential calls into O(graph depth) sequential rounds - PR-1
/// requires the analysis phase to complete in under a second even for a complex solution, which a
/// few hundred sequential MSBuild spawns cannot meet, but a few parallel rounds can.
let resolve (getReferences: ProjectReferenceResolver) (entryProject: string) : string list =
    let rec go (visited: Set<string>) (frontier: string list) (levels: string list list) : string list list =
        match frontier with
        | [] -> levels
        | _ ->
            let visited = frontier |> List.fold (fun v p -> Set.add p v) visited

            let nextFrontier =
                frontier
                |> Array.ofList
                |> Array.Parallel.map getReferences
                |> Array.toList
                |> List.concat
                |> List.distinct
                |> List.filter (fun p -> not (Set.contains p visited))

            go visited nextFrontier (frontier :: levels)

    go Set.empty [ entryProject ] [] |> List.rev |> List.concat
