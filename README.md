# dotnet-isolate

dotnet-isolate copies the minimal set of files needed to build a .NET project out of a larger
solution — the project, its transitive `ProjectReference` closure, and the files each needs — leaving
the original untouched. The result is a small Docker build context whose layer caches keep hitting.

## Usage

    dotnet isolate materialize path/to/<Project>.csproj [more.csproj ...] [-o <dir>] [-s <path>] [--restore] [--clean]
    dotnet isolate list-files  path/to/<Project>.csproj [more.csproj ...] [-s <path>] [--restore]

`materialize` copies the isolated closure into an output folder — this is what earlier versions did
implicitly. `list-files` prints the same resolved file set instead, one absolute path per line on
stdout, without writing anything (see "Skipping a rebuild when nothing changed" below).

- one or more entry projects; the result is the union of their closures
- `-o`/`--output-dir` (materialize only) — where to write (default `./<ProjectName>`); required for
  multiple projects
- `-s`/`--solution` — the solution to scope against, overriding the walk-up search
- `--restore` — emit/list only the files `dotnet restore` reads, for the split below
- `--clean` (materialize only) — delete the output directory first, instead of merging into it

Generated build artifacts are left behind: `bin/` and `obj/` next to a project file, `node_modules/`,
build logs and coverage reports, and anything under an `ArtifactsPath`. MSBuild resolves them as
ordinary items — a project whose directory contains other projects globs their output in, and a
hand-written `<None Include="**/*"/>` bypasses the SDK's exclusions entirely — but they are rewritten
on every local build, so shipping them would bust the layer cache the tool exists to protect.

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
        dotnet isolate materialize /src/Service1/Service1.csproj --restore -o /isolated/restore
    RUN --mount=type=bind,target=/src,source=. \
        dotnet isolate materialize /src/Service1/Service1.csproj -o /isolated/full

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
- **Add a `.dockerignore` for `bin/` and `obj/`.** The tool already keeps build artifacts out of the
  isolated output, but BuildKit keys the bind mount off the whole mounted subtree, so a local build
  before `docker build` still reruns the isolate stage. The layers below it stay cached either way.
- **Paths are relative to the computed mirror root**, not always the solution root — a file
  referenced from outside the solution folder raises it. Look at the output before writing your
  `COPY` and `publish` paths.

## Skipping a rebuild when nothing changed

`list-files` prints a service's exact dependency set — the same one `materialize` would place into
an output directory — so a CI step can skip an unchanged service's build/test stage entirely
instead of relying on coarse path filters (e.g. "did anything under src/Service1/ change", which
misses a shared library elsewhere in the closure and over-triggers on files the build never reads).

Paths are printed absolute; `git diff --name-only` reports repo-relative paths, so the recipe
relativizes before comparing:

    repo_root=$(git rev-parse --show-toplevel)
    deps=$(dotnet isolate list-files src/Service1/Service1.csproj | sed "s|^$repo_root/||" | sort)
    changed=$(git diff --name-only "$BASE_SHA" HEAD | sort)

    if comm -12 <(printf '%s\n' "$deps") <(printf '%s\n' "$changed") | grep -q .; then
      echo "dependencies changed — build required"
    else
      echo "no dependency changes — skipping build"
    fi

`BASE_SHA` is whatever your CI provider exposes for "the commit this branch diverged from" — e.g.
the merge-base with the target branch for a pull request.
