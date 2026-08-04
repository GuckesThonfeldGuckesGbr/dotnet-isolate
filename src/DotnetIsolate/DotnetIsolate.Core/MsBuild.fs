module DotnetIsolate.Core.MsBuild

open System.Diagnostics
open System.Text.Json

/// Runs `dotnet msbuild -getItem:<itemType>` against `projectPath` and returns the resolved
/// absolute paths (`FullPath`) of every item of that type - real MSBuild evaluation, so SDK-style
/// implicit globs, Directory.Build.props-injected items, and conditions all resolve correctly.
let getItemFullPaths (projectPath: string) (itemType: string) : string list =
    let psi =
        ProcessStartInfo(
            "dotnet",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        )

    psi.ArgumentList.Add("msbuild")
    psi.ArgumentList.Add(projectPath)
    psi.ArgumentList.Add($"-getItem:{itemType}")
    psi.ArgumentList.Add("-nologo")

    use proc = Process.Start(psi)
    let stdout = proc.StandardOutput.ReadToEnd()
    let stderr = proc.StandardError.ReadToEnd()
    proc.WaitForExit()

    if proc.ExitCode <> 0 then
        failwith $"dotnet msbuild -getItem:{itemType} failed for {projectPath} (exit {proc.ExitCode}): {stderr}"

    use doc = JsonDocument.Parse(stdout)
    let mutable items = Unchecked.defaultof<JsonElement>

    if doc.RootElement.GetProperty("Items").TryGetProperty(itemType, &items) then
        [ for item in items.EnumerateArray() -> item.GetProperty("FullPath").GetString() ]
    else
        []

/// A `ProjectGraph.ProjectReferenceResolver` backed by real MSBuild evaluation.
let projectReferenceResolver: ProjectGraph.ProjectReferenceResolver =
    fun projectPath -> getItemFullPaths projectPath "ProjectReference"
