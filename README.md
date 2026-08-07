# dotnet-isolate
dotnet-isolate isolates the exact set of projects and dependencies required to build a .NET project from a larger
solution. It helps reduce Docker build contexts, improve cache reuse, and speed up CI/CD pipelines without modifying the
original solution.

## Usage

    dotnet isolate path/to/<Project>.csproj

Creates a folder named `<Project>` (in the current directory) that mirrors just the relevant part
of the original solution tree — the project, its transitive project references, and the files each
of those needs to build. The output location can be changed with `-o`/`--output-dir`, which accepts
any relative or absolute path.

By default the tool finds your solution by walking up to the nearest `.sln`/`.slnx`, and prints
which one it picked. If more than one solution references the project, choose with
`-s`/`--solution`.

## What about files outside the solution folder?

The solution folder is the boundary, and which side of it a file sits on matters:

- **Files a project references explicitly** are always copied, even from outside. To keep their
  relative position, the output root rises to the common ancestor — so a
  `<Compile Include="../../shared/Version.cs"/>` adds levels to the isolated tree and shifts every
  path inside it. Run the tool once and look at the output before writing your `COPY` paths.
- **Files MSBuild finds by itself** — `.editorconfig`, `Directory.Build.props`,
  `Directory.Packages.props`, `NuGet.config`, `global.json` — are only collected from the solution
  folder down. Anything above it is ignored, because that search otherwise climbs all the way into
  your home directory.

So if a build file you need lives above the solution, point `-s` at a solution higher up the tree
(it still has to contain the projects you're isolating). That moves the boundary rather than
fighting it. Otherwise, reference the file from a project or from a `Directory.Build.props` inside
the solution folder, which brings it in explicitly.

One thing to know: `<Import Project="..."/>` is not followed. A `.props`/`.targets` file you import
by hand is only copied if it's one of the well-known names above, wherever it lives.

## How will this help me keep my docker image small?

There are two supported ways to wire this into a Dockerfile. Both work with **any** Docker builder,
classic or BuildKit — see DESIGN.md ("Docker integration patterns") for the mechanism, but in short:
a `COPY --from=<stage>` between build stages is always cached by checksumming the copied bytes, so
running `dotnet isolate` in its own stage and reaching it via `COPY --from` keeps the expensive
`restore`/`build`/`publish` steps cache-hit even when an unrelated file elsewhere in the solution
changes.

### Self-contained, two-stage (no host/CI setup beyond Docker)

    FROM mcr.microsoft.com/dotnet/sdk:8.0 AS isolate
    RUN dotnet tool install -g dotnet-isolate                       
    ENV PATH="$PATH:/root/.dotnet/tools"                            
    COPY . /src                                                     
    WORKDIR /src
    RUN dotnet isolate src/Service1/Service1.csproj -o /isolated    

    FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
    # Cache-hit whenever the isolated output is unchanged, even though the
    # `isolate` stage above just reran - COPY --from checksums the actual
    # copied bytes rather than chaining off that stage's own layer history.
    COPY --from=isolate /isolated /src
    WORKDIR /src
    RUN dotnet restore                                              
    RUN dotnet build -c Release --no-restore                        
    RUN dotnet publish src/Service1/Service1.csproj -c Release --no-build -o /app  

    FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS run
    COPY --from=build /app /app
    ENTRYPOINT ["dotnet", "/app/Service1.dll"]

### Host-side (no .NET SDK in any build stage)

Run on the host or CI runner before invoking Docker at all:

    dotnet tool install -g dotnet-isolate
    dotnet isolate src/Service1/Service1.csproj -o ./isolated

Then the Dockerfile just copies the already-isolated, deterministic output straight from the build
context — no tool install inside the image at all:

    FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
    COPY ./isolated /src                                            
    WORKDIR /src
    RUN dotnet restore                                              
    RUN dotnet build -c Release --no-restore                        
    RUN dotnet publish src/Service1/Service1.csproj -c Release --no-build -o /app

    FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS run
    COPY --from=build /app /app
    ENTRYPOINT ["dotnet", "/app/Service1.dll"]

## Design and requirements

Full requirements and design decisions live in [REQUIREMENTS.md](REQUIREMENTS.md) and
[DESIGN.md](DESIGN.md). The Dockerfile example above is illustrative, not literal — see those docs
for how the tool actually resolves and lays out an isolated project.