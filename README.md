# dotnet-isolate

dotnet-isolate copies the minimal set of files needed to build a .NET project out of a larger
solution — the project, its transitive `ProjectReference` closure, and the files each of those needs
— without modifying the original. The result is a small Docker build context whose layer caches keep
hitting.

## Usage

    dotnet isolate path/to/<Project>.csproj [more.csproj ...] [-o <dir>] [-s <path>] [--restore] [--clean]

- **Positional** — one or more entry projects; the output is the union of their closures.
- **`-o`/`--output-dir`** — any relative or absolute path. Defaults to `./<ProjectName>`, or
  `./<ProjectName>.restore` under `--restore`. Required with more than one entry project, since
  there is then no single name to default to.
- **`-s`/`--solution`** — template for the generated scoped solution file, and ceiling for
  implicitly-discovered build files. By default the tool walks up from the *first* entry project to
  the nearest `.sln`/`.slnx`, prints its choice, and warns if another entry lies outside it.
- **`--restore`** — emit only what `dotnet restore` reads: project files, the generated solution,
  `Directory.Build.props`/`.targets`, `Directory.Packages.props`, `NuGet.config`, `global.json`,
  `packages.lock.json`. Not `.editorconfig`: restore never reads it, and it would invalidate the
  cached restore layer on every formatting-rule edit.
- **`--clean`** — delete and recreate the output directory. The default is to merge into it and warn
  about entries the run did not produce; deleting by default was data loss, since with `-o` at the
  solution root it deleted the source tree and only then failed.

The tool also refuses an output directory that would consume its own inputs, ignores resolved inputs
under the output directory, and skips a referenced file that is absent from disk with a warning
rather than failing the run.

## How this keeps a Docker build cached

`COPY --from=<stage>` is keyed on the checksum of the bytes copied out of the source stage — **not**
on that stage's layer history. So the isolate stage can rerun on every build, as it must (its input
is the whole repo), while every layer below it cache-hits as long as the isolated output is
byte-identical. That checksum is content-based and mtime-insensitive, which is load-bearing: the
tool hardlinks where it can, so output files inherit their source's mtime, and a fresh CI clone
stamps every file with a new one. If mtime counted, none of this would work.

`--restore` splits the output where the cache should hold. The restore half has no sources in it, so
it changes only when a project file or a build-props file does, and `RUN dotnet restore` survives
ordinary source edits — an end-to-end test changes a file *inside* the isolated closure and asserts
`dotnet restore` is still `CACHED` while the build layer reruns.

    # syntax=docker/dockerfile:1
    FROM mcr.microsoft.com/dotnet/sdk:8.0 AS isolate
    RUN dotnet tool install -g dotnet-isolate
    ENV PATH="$PATH:/root/.dotnet/tools"
    RUN --mount=type=bind,target=/src,source=. \
        dotnet isolate /src/Service1/Service1.csproj --restore -o /isolated/restore
    RUN --mount=type=bind,target=/src,source=. \
        dotnet isolate /src/Service1/Service1.csproj -o /isolated/full

    FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
    WORKDIR /src
    COPY --from=isolate /isolated/restore /src
    RUN dotnet restore
    COPY --from=isolate /isolated/full /src
    RUN dotnet build -c Release --no-restore
    RUN dotnet publish Service1/Service1.csproj -c Release --no-build -o /app

    FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS run
    COPY --from=build /app /app
    ENTRYPOINT ["dotnet", "/app/Service1.dll"]

This needs buildx (Docker's default builder); the classic builder has no bind mounts. Three notes:

- The **bind mount replaces `COPY . /src`**: the tool only reads the source and writes to
  `/isolated`, and unlike `COPY` the mount keeps the whole build context out of the isolate stage's
  layers — the cost the tool exists to avoid.
- **Hardlinking across the bind mount fails.** One upfront probe detects that and falls back to
  copying. Nothing to configure.
- **Paths in the isolated tree are relative to the computed mirror root**, not always the solution
  root. Run the tool once and look before writing `COPY` and `publish` paths — above, the mirror
  root is the solution folder, so `Service1/Service1.csproj` has no `src/` prefix.

## What about files outside the solution folder?

The solution folder is the boundary. Files a project references **explicitly** are always copied,
even from outside — and to keep their relative position the mirror root rises to the common
ancestor, so a `<Compile Include="../../shared/Version.cs"/>` adds levels to the isolated tree and
shifts every path inside it. Files MSBuild finds **by itself** (`.editorconfig`,
`Directory.Build.props`, `Directory.Packages.props`, `NuGet.config`, `global.json`) are collected
from the solution folder down only, because that search otherwise climbs into your home directory.

So if a build file you need lives above the solution, point `-s` at a solution higher up the tree
(it still has to contain the projects you're isolating), or reference the file from a project or a
`Directory.Build.props` inside the solution folder. Note that `<Import Project="..."/>` is not
followed: a hand-imported `.props`/`.targets` is copied only if it has one of the names above.

Full requirements and design decisions live in [REQUIREMENTS.md](REQUIREMENTS.md) and
[DESIGN.md](DESIGN.md).
