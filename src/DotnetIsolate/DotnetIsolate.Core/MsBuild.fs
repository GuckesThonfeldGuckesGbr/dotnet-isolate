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

/// Runs `dotnet msbuild -getItem:<types>` against `projectPath` for all of `itemTypes` in a
/// single process call, and returns each type's resolved absolute paths (`FullPath`) - real
/// MSBuild evaluation, so SDK-style implicit globs, Directory.Build.props-injected items, and
/// conditions all resolve correctly. Item types with no matching items are omitted from the map.
let getItems (projectPath: string) (itemTypes: string list) : Map<string, string list> =
    let psi = ProcessStartInfo("dotnet")
    psi.ArgumentList.Add("msbuild")
    psi.ArgumentList.Add(projectPath)
    psi.ArgumentList.Add($"""-getItem:{String.concat "," itemTypes}""")
    psi.ArgumentList.Add("-nologo")
    // Node reuse leaves a persistent MSBuild server process behind; harmless for one call, but
    // many concurrent/nested calls (as happen across this tool's own test suite) can queue up on
    // shared nodes and stall. Not worth the reuse speedup here for correctness's sake.
    psi.ArgumentList.Add("-nodeReuse:false")

    let stdout, stderr, exitCode = runCapturingOutput psi

    if exitCode <> 0 then
        let types = String.concat "," itemTypes
        failwith $"dotnet msbuild -getItem:{types} failed for {projectPath} (exit {exitCode}): {stderr}"

    use doc = JsonDocument.Parse(stdout)
    let itemsElement = doc.RootElement.GetProperty("Items")

    itemTypes
    |> List.choose (fun itemType ->
        let mutable items = Unchecked.defaultof<JsonElement>

        if itemsElement.TryGetProperty(itemType, &items) then
            let paths = [ for item in items.EnumerateArray() -> item.GetProperty("FullPath").GetString() ]
            Some(itemType, paths)
        else
            None)
    |> Map.ofList

/// Runs `dotnet msbuild -getItem:<itemType>` and returns just that type's resolved absolute paths.
let getItemFullPaths (projectPath: string) (itemType: string) : string list =
    getItems projectPath [ itemType ] |> Map.tryFind itemType |> Option.defaultValue []

/// A `ProjectGraph.ProjectReferenceResolver` backed by real MSBuild evaluation.
let projectReferenceResolver: ProjectGraph.ProjectReferenceResolver =
    fun projectPath -> getItemFullPaths projectPath "ProjectReference"
