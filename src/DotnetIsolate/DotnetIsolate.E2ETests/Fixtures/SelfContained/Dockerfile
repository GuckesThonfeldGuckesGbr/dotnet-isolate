# DI-1 pattern A ("self-contained, two-stage"): dotnet-isolate runs inside its own build stage.
# Installs the tool from a local nupkg (built by the test harness from this branch's own code,
# via `nupkg/` in the build context) rather than nuget.org - the package isn't necessarily
# published yet, and this suite gates the very publish that would put it there.
#
# Targets ServiceA out of the DiamondWithIncludedFilesSln fixture (src/TestSolutions/), which
# targets net8.0 so no SDK-version gymnastics are needed here.
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS isolate
COPY nupkg /nupkg
# --prerelease: NBGV versions non-tag builds (like this one, packed straight from the branch under
# test) as prerelease, which `dotnet tool install` otherwise silently filters out.
RUN dotnet tool install -g dotnet-isolate --add-source /nupkg --prerelease
ENV PATH="$PATH:/root/.dotnet/tools"
COPY . /src
WORKDIR /src
RUN dotnet isolate ServiceA/ServiceA.csproj -o /isolated

FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
COPY --from=isolate /isolated /src
WORKDIR /src
RUN dotnet restore
RUN dotnet build -c Release --no-restore
RUN dotnet publish ServiceA/ServiceA.csproj -c Release --no-build -o /app

FROM mcr.microsoft.com/dotnet/runtime:8.0 AS run
COPY --from=build /app /app
ENTRYPOINT ["dotnet", "/app/ServiceA.dll"]
