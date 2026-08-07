module DotnetIsolate.Core.MsBuild

open System.Diagnostics
open System.Text.Json

/// Starts `psi` (which must not already redirect output) and reads stdout/stderr to completion,
/// returning both plus the exit code. Awaits both streams concurrently rather than draining stdout
/// before starting stderr - a child that writes enough to stderr to fill the OS pipe buffer before
/// finishing stdout would otherwise deadlock (it blocks writing stderr while we block reading
/// stdout).
let runCapturingOutput (psi: ProcessStartInfo) : string * string * int =
    psi.RedirectStandardOutput <- true
    psi.RedirectStandardError <- true
    psi.UseShellExecute <- false

    use proc = Process.Start(psi)

    let stdout, stderr =
        [ proc.StandardOutput.ReadToEndAsync(); proc.StandardError.ReadToEndAsync() ]
        |> List.map Async.AwaitTask
        |> Async.Parallel
        |> Async.RunSynchronously
        |> fun outputs -> outputs[0], outputs[1]

    proc.WaitForExit()
    stdout, stderr, proc.ExitCode

/// Runs `dotnet msbuild -getItem:<types> -getProperty:<names>` against `projectPath` for all of
/// `itemTypes` and `propertyNames` in a single process call, and returns each item type's resolved
/// absolute paths (`FullPath`) plus each property's evaluated value - real MSBuild evaluation, so
/// SDK-style implicit globs, Directory.Build.props-injected items, and conditions all resolve
/// correctly. Item types with no matching items are omitted from the item map; unset properties
/// come back as empty strings (MSBuild always emits a key for a requested property).
let getItemsAndProperties
    (projectPath: string)
    (itemTypes: string list)
    (propertyNames: string list)
    : Map<string, string list> * Map<string, string> =
    let psi = ProcessStartInfo("dotnet")
    psi.ArgumentList.Add("msbuild")
    psi.ArgumentList.Add(projectPath)

    if not (List.isEmpty itemTypes) then
        psi.ArgumentList.Add($"""-getItem:{String.concat "," itemTypes}""")

    if not (List.isEmpty propertyNames) then
        psi.ArgumentList.Add($"""-getProperty:{String.concat "," propertyNames}""")

    psi.ArgumentList.Add("-nologo")
    // Node reuse leaves a persistent MSBuild server process behind; harmless for one call, but
    // many concurrent/nested calls (as happen across this tool's own test suite) can queue up on
    // shared nodes and stall. Not worth the reuse speedup here for correctness's sake.
    psi.ArgumentList.Add("-nodeReuse:false")

    let stdout, stderr, exitCode = runCapturingOutput psi

    if exitCode <> 0 then
        let types = String.concat "," itemTypes
        let names = String.concat "," propertyNames
        failwith
            $"dotnet msbuild -getItem:{types} -getProperty:{names} failed for {projectPath} (exit {exitCode}): {stderr}"

    use doc = JsonDocument.Parse(stdout)

    let section (name: string) =
        let mutable element = Unchecked.defaultof<JsonElement>

        if doc.RootElement.TryGetProperty(name, &element) then Some element else None

    let items =
        match section "Items" with
        | None -> Map.empty
        | Some itemsElement ->
            itemTypes
            |> List.choose (fun itemType ->
                let mutable items = Unchecked.defaultof<JsonElement>

                if itemsElement.TryGetProperty(itemType, &items) then
                    let paths = [ for item in items.EnumerateArray() -> item.GetProperty("FullPath").GetString() ]
                    Some(itemType, paths)
                else
                    None)
            |> Map.ofList

    let properties =
        match section "Properties" with
        | None -> Map.empty
        | Some propertiesElement ->
            propertyNames
            |> List.choose (fun name ->
                let mutable value = Unchecked.defaultof<JsonElement>

                if propertiesElement.TryGetProperty(name, &value) then
                    Some(name, value.GetString())
                else
                    None)
            |> Map.ofList

    items, properties

/// Runs `dotnet msbuild -getItem:<types>` and returns each type's resolved absolute paths.
let getItems (projectPath: string) (itemTypes: string list) : Map<string, string list> =
    getItemsAndProperties projectPath itemTypes [] |> fst

/// Runs `dotnet msbuild -getItem:<itemType>` and returns just that type's resolved absolute paths.
let getItemFullPaths (projectPath: string) (itemType: string) : string list =
    getItems projectPath [ itemType ] |> Map.tryFind itemType |> Option.defaultValue []

/// A `ProjectGraph.ProjectReferenceResolver` backed by real MSBuild evaluation.
let projectReferenceResolver: ProjectGraph.ProjectReferenceResolver =
    fun projectPath -> getItemFullPaths projectPath "ProjectReference"
