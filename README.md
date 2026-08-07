# dotnet-isolate

Copy one .NET project out of a big solution, with everything it needs to build and nothing else.

## Why

Docker builds start with `COPY . /src`. That layer changes whenever *any* file in the repo changes,
so every `restore`/`build`/`publish` step after it is invalidated too — even when your service
didn't change at all.

`dotnet isolate` gives you a much smaller thing to copy, so unrelated changes stop busting the
cache.

## Usage

    dotnet isolate path/to/Service1.csproj

Writes a `./Service1` folder holding the project, its transitive project references, every file
those need to compile, the repo-level build files, and a solution scoped to just those projects.
The original directory layout is preserved, and the result builds on its own with `dotnet build`.

| Option | Meaning |
| --- | --- |
| `-o`, `--output-dir` | Where to write the output. Any relative or absolute path. |
| `-s`, `--solution` | Which solution to use as the template. Auto-discovered otherwise. |

## How it works

It asks MSBuild instead of guessing. Every project is evaluated with `dotnet msbuild`, so implicit
globs, `Directory.Build.props`, analyzer rulesets, and conditions resolve exactly as they do in a
real build. Output is deterministic, and hardlinked where the filesystem allows.

## Using it in Docker

Both patterns work under any builder, classic or BuildKit.

**Self-contained.** Nothing needed on the host but Docker. The `isolate` stage reruns every time,
but `build` still hits cache, because `COPY --from` is keyed on the copied bytes.

```dockerfile
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS isolate
RUN dotnet tool install -g dotnet-isolate
ENV PATH="$PATH:/root/.dotnet/tools"
COPY . /src
WORKDIR /src
RUN dotnet isolate src/Service1/Service1.csproj -o /isolated

FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
COPY --from=isolate /isolated /src
WORKDIR /src
RUN dotnet restore
RUN dotnet build -c Release --no-restore
RUN dotnet publish src/Service1/Service1.csproj -c Release --no-build -o /app

FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS run
COPY --from=build /app /app
ENTRYPOINT ["dotnet", "/app/Service1.dll"]
```

**Host-side.** Isolate before you build, and no SDK stage is needed for it.

```bash
dotnet tool install -g dotnet-isolate
dotnet isolate src/Service1/Service1.csproj -o ./isolated
```

```dockerfile
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
COPY ./isolated /src
WORKDIR /src
RUN dotnet restore
RUN dotnet build -c Release --no-restore
RUN dotnet publish src/Service1/Service1.csproj -c Release --no-build -o /app

FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS run
COPY --from=build /app /app
ENTRYPOINT ["dotnet", "/app/Service1.dll"]
```

## More

- [REQUIREMENTS.md](REQUIREMENTS.md) — numbered requirements.
- [DESIGN.md](DESIGN.md) — the pipeline, and why each step works the way it does.
