/// Writes results in the JSON shape benchmark-action/github-action-benchmark's
/// `customSmallerIsBetter` tool expects (a flat array of {name, unit, value}), so CI can chart
/// them over time and comment on a regression (.github/workflows/build.yml's `performance` job).
/// Appends across multiple calls within a run rather than overwriting, so more than one fact
/// (e.g. more than one [<Fact>] in this project) ends up on the same chart set.
module DotnetIsolate.PerformanceTests.BenchmarkResults

open System
open System.IO
open System.Text.Json
open System.Text.Json.Serialization

type Entry =
    { [<JsonPropertyName("name")>]
      Name: string
      [<JsonPropertyName("unit")>]
      Unit: string
      [<JsonPropertyName("value")>]
      Value: float }

/// Overridable via DOTNET_ISOLATE_BENCHMARK_OUTPUT so CI can point it at an absolute,
/// unambiguous path regardless of the test host's working directory; defaults to the test
/// assembly's own output directory for local runs.
let private outputPath =
    match Environment.GetEnvironmentVariable("DOTNET_ISOLATE_BENCHMARK_OUTPUT") with
    | null
    | "" -> Path.Combine(AppContext.BaseDirectory, "benchmark-results.json")
    | path -> path

let private lockObj = obj ()

/// Merges `entries` into whatever this run has already written to `outputPath` (by `Name`, last
/// write wins) and rewrites the file - safe to call from more than one test in the same run.
let write (entries: Entry list) =
    lock lockObj (fun () ->
        let existing =
            if File.Exists(outputPath) then
                JsonSerializer.Deserialize<Entry list>(File.ReadAllText(outputPath))
            else
                []

        let merged =
            (existing @ entries)
            |> List.map (fun e -> e.Name, e)
            |> Map.ofList
            |> Map.toList
            |> List.map snd

        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)) |> ignore
        File.WriteAllText(outputPath, JsonSerializer.Serialize(merged, JsonSerializerOptions(WriteIndented = true))))
