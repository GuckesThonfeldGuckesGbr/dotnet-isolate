module DotnetIsolate.E2ETests.DockerHarness

open System
open System.Diagnostics
open System.IO

/// Shells out to `docker <args>` and returns (exit code, combined stdout+stderr). Both builders
/// write meaningful progress to either stream depending on version/TTY detection, so the two are
/// combined rather than analyzed separately.
let private runDocker (useBuildKit: bool) (workingDir: string) (args: string list) =
    let psi =
        ProcessStartInfo(
            "docker",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            WorkingDirectory = workingDir
        )

    psi.Environment["DOCKER_BUILDKIT"] <- if useBuildKit then "1" else "0"
    args |> List.iter psi.ArgumentList.Add

    use proc = Process.Start(psi)
    let stdout = proc.StandardOutput.ReadToEnd()
    let stderr = proc.StandardError.ReadToEnd()
    proc.WaitForExit()
    proc.ExitCode, stdout + "\n" + stderr

/// Builds `dockerfilePath` against `contextDir`, tagged `tag`, under the requested builder.
/// `--progress=plain` keeps BuildKit output parseable regardless of TTY detection - the legacy
/// builder doesn't recognize the flag at all, so it's only passed when useBuildKit is set.
let runDockerBuild (useBuildKit: bool) (contextDir: string) (dockerfilePath: string) (tag: string) =
    let progressArgs = if useBuildKit then [ "--progress=plain" ] else []

    runDocker
        useBuildKit
        contextDir
        ([ "build" ] @ progressArgs @ [ "-f"; dockerfilePath; "-t"; tag; contextDir ])

/// Removes a built image; best-effort, used only for cleanup.
let removeImage (tag: string) =
    runDocker true "." [ "rmi"; "-f"; tag ] |> ignore

/// Both builders mark a cached step on the line right after the step's own command line - classic
/// with "Using cache" (after "Step N/M : ..."), BuildKit with "CACHED" (after "#N [stage] ...",
/// with --progress=plain). `stepNeedle` identifies the step by a distinctive substring of its
/// command (e.g. "dotnet restore"), since step numbers shift between the two Dockerfile patterns.
let private isStepCached (useBuildKit: bool) (output: string) (stepNeedle: string) =
    let marker = if useBuildKit then "CACHED" else "Using cache"
    let lines = output.Split('\n')

    lines
    |> Array.tryFindIndex (fun l -> l.Contains(stepNeedle))
    |> Option.map (fun i -> i + 1 < lines.Length && lines[i + 1].Contains(marker))
    |> Option.defaultValue false

/// True if every one of the expensive `restore`/`build`/`publish` RUN steps cache-hit on this
/// build's output - the actual DI-1 mechanism assertion.
let expensiveLayersCached (useBuildKit: bool) (output: string) =
    [ "dotnet restore"; "dotnet build -c Release"; "dotnet publish" ]
    |> List.forall (isStepCached useBuildKit output)

/// True if the given step did NOT cache-hit - used as a sanity check that an unrelated-file
/// change genuinely invalidated something, so the cache-hit assertion above isn't vacuous.
let stepNotCached (useBuildKit: bool) (output: string) (stepNeedle: string) =
    not (isStepCached useBuildKit output stepNeedle)

/// True if the given step cache-hit. BuildKit only - the classic builder is no longer supported.
let stepCached (output: string) (stepNeedle: string) = isStepCached true output stepNeedle

/// Recursively copies `sourceDir` into a fresh, uniquely-named temp directory and returns its
/// path, skipping bin/obj (build artifacts, not part of the fixture).
let copyFixtureToTempDir (sourceDir: string) =
    let dest = Path.Combine(Path.GetTempPath(), "dotnet-isolate-e2e", Path.GetRandomFileName())
    Directory.CreateDirectory(dest) |> ignore

    let rec copy (src: string) (dst: string) =
        Directory.CreateDirectory(dst) |> ignore

        for file in Directory.GetFiles(src) do
            File.Copy(file, Path.Combine(dst, Path.GetFileName(file)))

        for dir in Directory.GetDirectories(src) do
            let name = Path.GetFileName(dir)
            if name <> "bin" && name <> "obj" then
                copy dir (Path.Combine(dst, name))

    copy sourceDir dest
    dest

/// Deletes a directory tree if it exists; used for temp-dir cleanup.
let deleteIfExists (dir: string) =
    if Directory.Exists(dir) then
        Directory.Delete(dir, recursive = true)

/// Appends a harmless, uniquely-identified comment to a file, to prove the early, uncacheable step
/// (the "isolate"/COPY . /src stage) really does rerun between builds - without this, a cache-hit
/// assertion on the downstream layers would be vacuous.
///
/// The appended text embeds a fresh GUID rather than a fixed literal. BuildKit's build cache is
/// content-addressed and shared across the whole daemon, not scoped to a single test or process:
/// two builds anywhere that happen to produce byte-identical input trees will share a cache entry
/// regardless of which invocation produced it first. A fixed literal made every run's "touched"
/// state identical to every other run's, which cuts both ways and both are bad:
///   - a false red: on a warm daemon, a *correct* build's "should rerun" sanity check can find a
///     cache entry left by an earlier run's identical touch and wrongly appear still-cached.
///   - a false green, which is worse: if a future regression made the restore half leak a source
///     file, a warm daemon could satisfy `stepCached ... "dotnet restore"` from a cache entry a
///     previous run of that same regression already populated with that same fixed literal - the
///     assertion would pass by coincidence, masking the exact defect this suite exists to catch.
/// A GUID makes every call's content unique, so no two runs - buggy or correct - can ever collide
/// on a cache key, closing off both failure modes.
let touchUnrelatedFile (path: string) =
    File.AppendAllText(path, $"\n// e2e: unrelated change {Guid.NewGuid():N}\n")

/// Packs `toolProjectPath` (this branch's own DotnetIsolate.fsproj) into `destNupkgDir`, so the
/// self-contained Dockerfile pattern can `dotnet tool install` from a local nupkg instead of
/// nuget.org - the package isn't necessarily published yet, and this suite gates that publish.
let packLocalTool (toolProjectPath: string) (destNupkgDir: string) =
    Directory.CreateDirectory(destNupkgDir) |> ignore

    let psi =
        ProcessStartInfo(
            "dotnet",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        )

    [ "pack"; toolProjectPath; "-c"; "Release"; "-o"; destNupkgDir ]
    |> List.iter psi.ArgumentList.Add

    use proc = Process.Start(psi)
    let stdout = proc.StandardOutput.ReadToEnd()
    let stderr = proc.StandardError.ReadToEnd()
    proc.WaitForExit()

    if proc.ExitCode <> 0 then
        failwith $"dotnet pack failed (exit {proc.ExitCode}):\n{stdout}\n{stderr}"
