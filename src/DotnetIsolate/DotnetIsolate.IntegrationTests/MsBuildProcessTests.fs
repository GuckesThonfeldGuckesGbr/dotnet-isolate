module DotnetIsolate.IntegrationTests.MsBuildProcessTests

open System
open System.Diagnostics
open System.IO
open System.Threading.Tasks
open Xunit
open DotnetIsolate.Core

/// Reproduces the scenario from the code review: a child process that writes enough to stderr to
/// fill the OS pipe buffer before it finishes writing stdout. Sequential `ReadToEnd` calls (stdout
/// fully drained before stderr is even started) deadlock here - the child blocks writing stderr
/// while the parent blocks reading stdout. Uses `dotnet fsi` rather than a shell command so the
/// repro is cross-platform (no bash/pwsh dependency).
[<Fact>]
let ``runCapturingOutput does not deadlock when a process writes heavy stderr before stdout`` () : Task =
    task {
        let script = Path.GetTempFileName() + ".fsx"

        File.WriteAllText(
            script,
            "eprintf \"%s\" (String.replicate 2_000_000 \"x\")\n\
             printfn \"done\"\n"
        )

        try
            let psi = ProcessStartInfo("dotnet")
            psi.ArgumentList.Add("fsi")
            psi.ArgumentList.Add(script)

            let work = Task.Run(fun () -> MsBuild.runCapturingOutput psi)
            let! winner = Task.WhenAny(work, Task.Delay(TimeSpan.FromSeconds 30.0))

            Assert.True(obj.ReferenceEquals(winner, work), "runCapturingOutput deadlocked instead of completing")

            let! _, _, exitCode = work
            Assert.Equal(0, exitCode)
        finally
            File.Delete(script)
    }
