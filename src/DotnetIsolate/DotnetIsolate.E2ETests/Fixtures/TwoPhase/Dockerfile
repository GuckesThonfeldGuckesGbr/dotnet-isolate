# syntax=docker/dockerfile:1
# The pattern the README documents: buildx only, the source reaching the isolate stage through a
# read-only bind mount rather than COPY (which would pull the whole context into the stage's
# layers), and the output split so the restore layer survives ordinary source edits.
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS isolate
COPY nupkg /nupkg
# --prerelease: NBGV versions non-tag builds as prerelease, which `dotnet tool install` otherwise
# silently filters out.
RUN dotnet tool install -g dotnet-isolate --add-source /nupkg --prerelease
ENV PATH="$PATH:/root/.dotnet/tools"
RUN --mount=type=bind,target=/src,source=. \
    dotnet isolate materialize /src/ServiceA/ServiceA.csproj --restore -o /isolated/restore
RUN --mount=type=bind,target=/src,source=. \
    dotnet isolate materialize /src/ServiceA/ServiceA.csproj -o /isolated/full

FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src
COPY --from=isolate /isolated/restore /src
RUN dotnet restore
COPY --from=isolate /isolated/full /src
RUN dotnet build -c Release --no-restore
RUN dotnet publish ServiceA/ServiceA.csproj -c Release --no-build -o /app

FROM mcr.microsoft.com/dotnet/runtime:8.0 AS run
COPY --from=build /app /app
ENTRYPOINT ["dotnet", "/app/ServiceA.dll"]
