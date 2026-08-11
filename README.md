# dotnet-isolate

dotnet-isolate copies the minimal set of files needed to build a .NET project out of a larger
solution — the project, its transitive `ProjectReference` closure, and the files each needs — leaving
the original untouched. The result is a small Docker build context whose layer caches keep hitting.

## Usage

    dotnet isolate path/to/<Project>.csproj [more.csproj ...] [-o <dir>] [-s <path>] [--restore] [--clean]

- one or more entry projects; the output is the union of their closures
- `-o`/`--output-dir` — where to write (default `./<ProjectName>`); required for multiple projects
- `-s`/`--solution` — the solution to scope against, overriding the walk-up search
- `--restore` — emit only the files `dotnet restore` reads, for the split below
- `--clean` — delete the output directory first, instead of merging into it

Defaults, warnings and output-directory safety: [REQUIREMENTS.md](REQUIREMENTS.md). File resolution
and layout: [DESIGN.md](DESIGN.md).

## How this keeps a Docker build cached

`COPY --from=<stage>` is keyed on the checksum of the bytes copied out of the source stage — **not**
on that stage's layer history. So the isolate stage can rerun on every build, as it must, while every
layer below it cache-hits as long as the isolated output is byte-identical. That checksum is also
mtime-insensitive, which is load-bearing: the tool hardlinks, so output files inherit their source's
mtime, and a fresh CI clone stamps every file with a new one.

`--restore` splits the output where the cache should hold: the restore half has no sources in it, so
`RUN dotnet restore` survives ordinary source edits. An end-to-end test changes a file *inside* the
isolated closure and asserts `dotnet restore` is still `CACHED` while the build layer reruns.

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

Needs buildx (Docker's default builder); the classic builder has no bind mounts. The last stage is
just your app's runtime image — pick the base it needs, nothing about the caching depends on it.

- The read-only **bind mount replaces `COPY . /src`**, keeping the whole build context out of the
  isolate stage's layers — the cost the tool exists to avoid.
- **Hardlinking across the bind mount fails**; one upfront probe detects that and copies instead.
- **Paths are relative to the computed mirror root**, not always the solution root — a file
  referenced from outside the solution folder raises it. Look at the output before writing your
  `COPY` and `publish` paths.
